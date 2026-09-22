// Scenario runner for the bridge's runtime lifetime rules.
//
// This is the one part of the bridge a host cannot check by reading: whether
// `cultmesh_quic_runtime_close` is safe against host threads that are inside the
// library when it starts. Everything here drives that path and nothing else. It
// opens a runtime, parks pollers in `cultmesh_quic_next_event`, closes the
// runtime underneath them, and asserts what the header promises: every parked
// poller returns, and none of them touches the runtime after it is gone.
//
// It consumes the bridge through include/cultmesh_quic_native.h — the same
// contract a host has — and nothing else. No MsQuic header, no internals.
//
// The failures it exists to catch are not visible in the return values, because
// a use-after-free is not an error code. Two things make them visible instead:
// a sanitizer build (ThreadSanitizer for the notify/destroy race, AddressSanitizer
// for the freed-runtime dereference), and `CULTMESH_QUIC_DEBUG_ASSERTS`, which
// makes the bridge state the quiesce invariant itself rather than leaving it to
// be caught by an unlucky thread schedule.
//
//   cultmesh_quic_native_tests closerace [iterations] [pollers]
//   cultmesh_quic_native_tests holdclose [iterations] [pollers]
//   cultmesh_quic_native_tests polltimeout [iterations]
//   cultmesh_quic_native_tests pollbusy [iterations]
//   cultmesh_quic_native_tests latecall [iterations]
//   cultmesh_quic_native_tests holdtimeout [iterations]
//   cultmesh_quic_native_tests waitseam [iterations]
//   cultmesh_quic_native_tests pollhammer [iterations]
//   cultmesh_quic_native_tests zerotimeout [iterations]
//   cultmesh_quic_native_tests payloadfit [iterations]
//
// `closerace` hunts the race: many pollers, many iterations, and whatever the
// schedule gives. `holdclose` takes the guessing out of it — the bridge's
// development seam (section 7 of the header) holds the pollers inside the
// library until this scenario releases them, and reports how many host calls the
// bridge actually counted, so the scenario asserts the quiesce instead of
// assuming its own fixture worked. It needs CULTMESH_QUIC_DEBUG_ASSERTS.
//
// In both of those the wait is ended by the close, so the host's timeout governs
// nothing either of them can see, and a bridge waiting on a constant of its own
// passes both. `polltimeout` is the one that lets the timeout govern the return
// and measures it, and `pollbusy` measures it again with the host's other thread
// calling in throughout, which wakes the wait. `latecall` covers the other half
// of section 4: a call that races the start of a close is refused rather than
// counted behind its wait.
// `holdtimeout` covers the seam's own rule, which nothing else is in a position
// to see: the hold parks a call the wait woke and not one whose own timeout
// expired, so the quiesce the hold scenarios assert is the bridge's doing and
// not the fixture's.
//
// Self's ruling of 2026-09-22, after seven Soul passes against `polltimeout`,
// `pollbusy` and `holdtimeout` alone: a wall-clock probe cannot tell the host's
// timeout from any function of it that is the identity at the probe's own
// value, because there is always another function that matches. `waitseam`
// puts the observation where the rule is decided instead of where its effect
// eventually shows: it reads `cultmesh_quic_debug_last_wait_ms`, the bridge's
// own account of what it handed its condition wait, and asserts equality
// against the host's argument, deterministically, with no timing tolerance.
// `polltimeout`, `pollbusy` and `holdtimeout` stay only as generous-margin
// proof that the recorded wait is really waited and not merely recorded.
// `pollhammer` covers the wait's predicate the same way `pollbusy` used to try
// to: one thread hammers every gate-touching entry point that cannot itself
// queue an event, in a yield loop, while another holds a single idle poll, and
// the poll must still stay for its timeout.
//
// Exit code 0 means every assertion held. Anything else, including a sanitizer
// abort or the bridge's own assertion, is a failure. Under ThreadSanitizer run
// it with `TSAN_OPTIONS=halt_on_error=1`, or a reported race leaves the exit
// code to the last thing that set it.

#include <cultmesh_quic_native.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <climits>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace {

// Long enough that a poller is still parked when the close begins. The close is
// what ends the wait; this timeout only bounds the scenario if the close never
// arrives, which is itself the failure.
constexpr int32_t kPollTimeoutMs = 5000;

// A poller asks for a payload buffer, so the close races a `next_event` that has
// somewhere to copy into rather than one that can only ever return 0.
constexpr int32_t kPayloadCapacity = 4096;

int Fail(const std::string& message) {
    std::fprintf(stderr, "FAILED: %s\n", message.c_str());
    return 1;
}

// One iteration: `pollers` threads parked in `next_event`, then a close under
// them. Returns an empty string when every promise held.
//
// Each poller makes exactly one blocking call and does not call again. Section 5
// of the header puts the ordering on the host — no call may *begin* during a
// close — so a scenario that kept re-entering would be testing a shape the
// bridge declines to support, and its report would be the harness's own fault.
// A call already inside the library is the case the quiesce exists for, and that
// is the one run here.
std::string CloseRaceOnce(int pollers) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    // Counts threads about to enter the blocking call. The close waits for all of
    // them, so the race is run with the pollers actually inside rather than with
    // threads that have not started yet.
    std::atomic<int> entering{0};
    std::atomic<int> returned{0};
    std::atomic<int> undefined_return{0};
    std::atomic<int> timed_out{0};
    // Set just before the close, and read by each poller as it returns. A poller
    // that is already out by then was never in the race, and a scenario full of
    // those passes without testing anything: this is what catches a bridge whose
    // blocking crossing has stopped blocking.
    std::atomic<bool> closing_started{false};
    std::atomic<int> left_before_the_close{0};

#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
    // Reset before any poller thread exists, so nothing here can race a
    // `CallScope` that has already been counted: `cultmesh_quic_next_event`
    // raises `debug_peak_calls` the moment its `CallScope` is constructed, at
    // the very top of the call, and a reset performed even microseconds after
    // that construction would zero a count no further call is coming to raise
    // again, stranding the settle loop below at "not yet" until its deadline.
    cultmesh_quic_debug_hold_calls(1);
    cultmesh_quic_debug_hold_calls(0);
#endif

    std::vector<std::thread> threads;
    threads.reserve(static_cast<size_t>(pollers));
    for (int index = 0; index < pollers; ++index) {
        threads.emplace_back([runtime, &entering, &returned, &undefined_return, &timed_out,
                              &closing_started, &left_before_the_close] {
            std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
            cultmesh_quic_event event{};
            int32_t required = 0;

            entering.fetch_add(1);
            const auto started = std::chrono::steady_clock::now();
            // Parked here when the close arrives. `closing` wakes the wait; the
            // call scope this thread holds is what the close must count out
            // before it frees anything.
            const int32_t result = cultmesh_quic_next_event(
                runtime, kPollTimeoutMs, &event, payload.data(), kPayloadCapacity, &required);
            const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - started).count();

            if (result != 0 && result != 1 && result != 2) undefined_return.fetch_add(1);
            // The close is what ends this wait. Sitting out the whole timeout
            // instead means the wake never reached it.
            if (elapsed >= kPollTimeoutMs / 2) timed_out.fetch_add(1);
            if (!closing_started.load()) left_before_the_close.fetch_add(1);
            returned.fetch_add(1);
        });
    }

    while (entering.load() < pollers) std::this_thread::yield();
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
    // A fixed settle races CPU starvation: under enough contending threads a
    // poller that has only incremented `entering` can sit preempted for far
    // longer than any sleep this scenario could afford, so the close would
    // begin before it ever reached the blocking wait — not the race this
    // scenario exists to run, just a fixture that gave up too soon. Where the
    // development seam is compiled in, this reads the bridge's own count
    // instead of guessing at a duration: the settle ends only once every
    // poller is actually counted inside, however long the scheduler took.
    const auto counted_deadline =
        std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (cultmesh_quic_debug_peak_calls() < pollers &&
           std::chrono::steady_clock::now() < counted_deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    // `active_calls` — what `peak_calls` reflects — is raised the moment a
    // poller's `CallScope` is constructed, at the top of
    // `cultmesh_quic_next_event`, not when it reaches the condition wait a few
    // statements later. Counted-inside is the expensive part to wait for under
    // starvation (getting the scheduler to run a thread at all); a short fixed
    // settle covers the cheap remainder (a lock acquisition and an empty-queue
    // check) once every poller is already scheduled and running.
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
#else
    // No seam in this configuration (the sanitizer build deliberately ships
    // without one), so the settle falls back to a longer fixed window rather
    // than the 50 ms that starved under 16 contending burners. It is still a
    // window, not a guarantee — the honest limit of a config with no way to
    // ask the bridge what it has actually counted.
    std::this_thread::sleep_for(std::chrono::milliseconds(500));
#endif

    closing_started.store(true);
    cultmesh_quic_runtime_close(runtime);
    for (auto& thread : threads) thread.join();

    if (left_before_the_close.load() != 0)
        return std::to_string(left_before_the_close.load()) + " of " + std::to_string(pollers) +
            " pollers returned before the close began, so they were never in the race";
    if (undefined_return.load() != 0)
        return std::to_string(undefined_return.load()) + " poller(s) saw a return the header does not define";
    if (timed_out.load() != 0)
        return std::to_string(timed_out.load()) + " of " + std::to_string(pollers) +
            " pollers sat out the timeout instead of being woken by the close";
    if (returned.load() != pollers)
        return std::to_string(returned.load()) + " of " + std::to_string(pollers) + " pollers returned";
    return {};
}

// The three timeouts `polltimeout` asks for: a spread, not a hunt for any
// particular derivation. `waitseam` is what proves the bridge hands its
// condition wait the host's own argument, exactly, at every value that matters
// — these three exist only to prove that value is then really waited on, so
// the spread just needs to be a short one, an ordinary one and a long one.
constexpr int32_t kShortestPollMs = 15;
constexpr int32_t kShortPollMs = 200;
constexpr int32_t kLongPollMs = 7300;

// How much sooner than the timeout a poll may return. A wait may be late; it may
// not be early, because returning early is exactly what waiting on a shorter
// duration looks like. This is scheduler granularity, not slack.
constexpr int kEarlyToleranceMs = 20;

// How much later than the timeout a poll may return. Generous on purpose: these
// scenarios are no longer where a clamp, a floor, a round or a scale is caught
// — `waitseam` catches those by equality, with no clock involved — so this only
// has to catch a wait that is wrong by a lot: a constant of the bridge's own, a
// wait that never happens, a hold that parks a call its own timeout already
// ended, or the W1-W3 family Self's ruling of 2026-09-22 added, which apply
// their arithmetic to the duration `wait_for` actually receives *after* the
// seam has already recorded it and so cannot be caught by `waitseam`'s
// equality check at all. 400 ms sits above the worst overshoot a 16-burner
// stress pass measured on a loaded win32 host (180 ms; 27 ms on linux) and
// below every one of W1-W3's smallest offsets (485 ms), so one flat margin
// still serves every probe this file times without going blind to the family
// it was widened for.
constexpr int kGenerousLateToleranceMs = 400;

struct TimedPoll {
    int32_t result;
    long long elapsed_ms;
};

TimedPoll PollFor(void* runtime, int32_t timeout_ms) {
    std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
    cultmesh_quic_event event{};
    int32_t required = 0;
    const auto started = std::chrono::steady_clock::now();
    const int32_t result = cultmesh_quic_next_event(
        runtime, timeout_ms, &event, payload.data(), kPayloadCapacity, &required);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started).count();
    return {result, elapsed};
}

// The host's timeout, and the only thing it is: how long a poll with nothing to
// deliver stays inside the library. Nothing else here checks it — `closerace`
// and `holdclose` both end their waits with a close, so in both of them the
// bridge could wait on any duration at all and every assertion would still hold.
// A runtime with no listener and no connection has nothing to deliver, so the
// timeout is what ends this poll, and the elapsed time is what it waited on.
// The bands every timed poll is held to, and the worst overshoot seen, which is
// printed so a tolerance can be checked against a machine under load rather than
// trusted.
struct Overshoot {
    long long worst_ms = LLONG_MIN;
    void Record(const TimedPoll& poll, int32_t timeout_ms) {
        worst_ms = (std::max)(worst_ms, poll.elapsed_ms - timeout_ms);
    }
};

std::string CheckTimedPoll(const TimedPoll& poll, int32_t timeout_ms, int late_tolerance_ms) {
    const std::string asked = "a poll asking for " + std::to_string(timeout_ms) + " ms ";
    if (poll.result != 0)
        return asked + "returned " + std::to_string(poll.result) + ", not the 0 an idle runtime owes it";
    if (poll.elapsed_ms < timeout_ms - kEarlyToleranceMs)
        return asked + "returned after " + std::to_string(poll.elapsed_ms) +
            " ms: the wait ended on some shorter duration than the one it was given";
    if (poll.elapsed_ms > static_cast<long long>(timeout_ms) + late_tolerance_ms)
        return asked + "returned after " + std::to_string(poll.elapsed_ms) +
            " ms: the wait ended on some longer duration than the one it was given";
    return {};
}

constexpr std::array<int32_t, 3> kTimedProbesMs{kShortestPollMs, kShortPollMs, kLongPollMs};

std::string PollTimeoutOnce(std::array<Overshoot, 3>& overshoot) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    std::string failure;
    for (size_t probe = 0; probe < kTimedProbesMs.size() && failure.empty(); ++probe) {
        const TimedPoll poll = PollFor(runtime, kTimedProbesMs[probe]);
        overshoot[probe].Record(poll, kTimedProbesMs[probe]);
        failure = CheckTimedPoll(poll, kTimedProbesMs[probe], kGenerousLateToleranceMs);
    }

    cultmesh_quic_runtime_close(runtime);
    return failure;
}

int PollTimeout(int iterations) {
    std::array<Overshoot, 3> overshoot{};
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = PollTimeoutOnce(overshoot);
        if (!failure.empty())
            return Fail("polltimeout iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("polltimeout %dx: ok (worst overshoot %lld ms at %d, %lld ms at %d, %lld ms at %d)\n",
        iterations, overshoot[0].worst_ms, kTimedProbesMs[0], overshoot[1].worst_ms, kTimedProbesMs[1],
        overshoot[2].worst_ms, kTimedProbesMs[2]);
    return 0;
}

// How long `pollbusy`'s poll asks for, and how often the host's other thread
// calls into the library while it waits.
constexpr int32_t kBusyPollMs = 1000;
constexpr int kBusyCallEveryMs = 20;

// The same rule as `polltimeout` — a poll with nothing to deliver stays for its
// timeout — for a host with more than one thread, which is every real host: one
// thread polls, another sends, shuts streams down, reads the last error.
//
// Every host call leaves through a scope that wakes every waiter on the
// runtime's condition variable, because that is how the close learns a call has
// left. So a poll is woken each time any other thread makes any call, and what
// keeps it inside is the wait's predicate: nothing queued and no close means
// wait again, for the rest of the time the host asked for. With one host thread
// nothing else ever calls during the wait, and the predicate could be deleted
// with every other scenario still green.
//
// The other thread keeps calling for twice the poll's timeout and then stops,
// so a wait that restarts its whole timeout on every wake is seen as late
// rather than hanging the scenario.
std::string PollBusyOnce(Overshoot& overshoot) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    std::atomic<int> calls{0};
    std::atomic<int> refused{0};
    std::thread other([runtime, &calls, &refused] {
        const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(2 * kBusyPollMs);
        std::array<char, 256> message{};
        while (std::chrono::steady_clock::now() < until) {
            if (cultmesh_quic_last_error(runtime, message.data(), static_cast<int32_t>(message.size())) < 0)
                refused.fetch_add(1);
            calls.fetch_add(1);
            std::this_thread::sleep_for(std::chrono::milliseconds(kBusyCallEveryMs));
        }
    });

    const TimedPoll poll = PollFor(runtime, kBusyPollMs);
    const int calls_during = calls.load();
    other.join();
    cultmesh_quic_runtime_close(runtime);

    overshoot.Record(poll, kBusyPollMs);
    if (refused.load() != 0)
        return std::to_string(refused.load()) + " call(s) from the host's other thread were refused "
            "by a runtime that was not closing";
    // The timing first: a poll that left on the first wake returns before the
    // other thread has had time to make many calls, and that is the bridge
    // failing, not the fixture.
    const std::string failure = CheckTimedPoll(poll, kBusyPollMs, kGenerousLateToleranceMs);
    if (!failure.empty()) return failure + ", while the host's other thread was calling in";
    // A poll that passed with fewer calls than this was never woken, and passed
    // because the fixture did nothing rather than because the bridge held.
    if (calls_during < 3)
        return "the host's other thread made " + std::to_string(calls_during) +
            " call(s) during the poll, so nothing woke it";
    return {};
}

int PollBusy(int iterations) {
    Overshoot overshoot{};
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = PollBusyOnce(overshoot);
        if (!failure.empty())
            return Fail("pollbusy iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("pollbusy %dx: ok (worst overshoot %lld ms at %d)\n", iterations, overshoot.worst_ms,
        kBusyPollMs);
    return 0;
}

// Nothing listens on the discard port, so a connection to it fails and the
// bridge says why. That reason is the payload this scenario needs, and reaching
// it takes no listener, no credential and no established connection.
constexpr uint16_t kClosedPort = 9;

// How long the failure is given to arrive. The loopback refusal is immediate;
// if the refusal is swallowed instead, MsQuic's own handshake timeout ends the
// connection at 10 seconds and event 4 still arrives, so this only bounds a
// bridge that reports nothing at all.
constexpr int kEventDeadlineMs = 20000;

// The bridge writes its own reason sentences and every one of them starts here.
// Checking the bytes, and not only the length, is what separates a payload
// copied out of the queued event from one copied out of an event that was popped
// first: the second reads freed memory, which does not spell this.
constexpr char kReasonPrefix[] = "CultMesh QUIC connection";

// The two-phase poll, which is the shape every host uses to size its buffer:
// ask with nothing, be told what it needs, ask again. What makes it work is that
// the refusal consumes nothing — the event is still queued, payload and all,
// after a return of 2 — and that the copy happens before the event is popped.
// Both are section 6 of the header, and nothing committed reached either.
std::string PayloadFitOnce() {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    std::string failure;
    uint64_t connection_id = 0;
    const int32_t connecting =
        cultmesh_quic_connection_open(runtime, "127.0.0.1", kClosedPort, &connection_id);
    if (connecting != 0) {
        cultmesh_quic_runtime_close(runtime);
        return "cultmesh_quic_connection_open returned " + std::to_string(connecting);
    }

    // An out parameter the bridge must not write on a refusal, filled with
    // something the bridge never writes so that a write is visible.
    cultmesh_quic_event poisoned{};
    std::memset(&poisoned, 0xab, sizeof(poisoned));
    cultmesh_quic_event event = poisoned;
    int32_t required = 0;
    int32_t result = 0;

    // Ask with no room at all until the event carrying the failure reason
    // arrives. An event with no payload fits in nothing, so it is delivered and
    // polled past rather than mistaken for this one.
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(kEventDeadlineMs);
    for (;;) {
        event = poisoned;
        required = 0;
        result = cultmesh_quic_next_event(runtime, 250, &event, nullptr, 0, &required);
        if (result == 2 && required > 0) break;
        if (result < 0) {
            failure = "cultmesh_quic_next_event returned " + std::to_string(result);
            break;
        }
        if (std::chrono::steady_clock::now() >= deadline) {
            failure = "no event carrying a payload arrived within " +
                std::to_string(kEventDeadlineMs) + " ms of a connection to a closed port";
            break;
        }
    }

    if (failure.empty() && std::memcmp(&event, &poisoned, sizeof(event)) != 0)
        failure = "the refusal wrote to *out_event, which the header says it leaves alone";

    // Again, with the same empty buffer: a refusal that consumed the event
    // leaves nothing to find the second time.
    if (failure.empty()) {
        cultmesh_quic_event again = poisoned;
        int32_t required_again = 0;
        const int32_t second = cultmesh_quic_next_event(runtime, 0, &again, nullptr, 0, &required_again);
        if (second != 2 || required_again != required)
            failure = "the second ask returned " + std::to_string(second) + " needing " +
                std::to_string(required_again) + ", not the 2 needing " + std::to_string(required) +
                " the first one did: the refusal consumed the event";
    }

    // And with room, which is where the copy happens. The tail beyond the
    // payload is written too, so a copy that ran past what it was told to write
    // is visible.
    if (failure.empty()) {
        constexpr uint8_t kTail = 0x5a;
        std::vector<uint8_t> payload(static_cast<size_t>(required) + 16, kTail);
        cultmesh_quic_event delivered = poisoned;
        int32_t required_delivered = 0;
        const int32_t third = cultmesh_quic_next_event(
            runtime, 0, &delivered, payload.data(), static_cast<int32_t>(payload.size()),
            &required_delivered);
        const std::string reason(reinterpret_cast<const char*>(payload.data()),
            static_cast<size_t>(required));
        if (third != 1 || required_delivered != required)
            failure = "the ask with room returned " + std::to_string(third) + " needing " +
                std::to_string(required_delivered) + ", not the 1 needing " +
                std::to_string(required) + " the refusal promised";
        else if (delivered.type != CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN ||
                 delivered.connection_id != connection_id ||
                 delivered.payload_length != required)
            failure = "the delivered event was type " + std::to_string(delivered.type) +
                " on connection " + std::to_string(delivered.connection_id) + " carrying " +
                std::to_string(delivered.payload_length) + " byte(s), not the failed connection's own";
        else if (reason.rfind(kReasonPrefix, 0) != 0)
            failure = "the payload delivered was '" + reason + "', not a reason this bridge writes";
        else if (payload[static_cast<size_t>(required)] != kTail)
            failure = "the copy wrote past the " + std::to_string(required) + " bytes it reported";
    }

    // Consumed now, and only now.
    if (failure.empty()) {
        cultmesh_quic_event emptied = poisoned;
        int32_t required_emptied = 0;
        const int32_t fourth =
            cultmesh_quic_next_event(runtime, 0, &emptied, nullptr, 0, &required_emptied);
        if (fourth != 0)
            failure = "the event was still queued after being delivered: a further ask returned " +
                std::to_string(fourth);
    }

    cultmesh_quic_runtime_close(runtime);
    return failure;
}

int PayloadFit(int iterations) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = PayloadFitOnce();
        if (!failure.empty())
            return Fail("payloadfit iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("payloadfit %dx: ok\n", iterations);
    return 0;
}

#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)

// How long a poll that does not block is given to prove it by returning, after
// the calls have been counted and before the close begins. A bridge whose
// blocking wait no longer blocks has emptied itself of host calls by the time
// the close starts, and `calls_at_close` then says 0.
constexpr int kSettleMs = 100;

// How long the close is required to stay inside `cultmesh_quic_runtime_close`
// while the pollers are held. The held calls are what it is waiting for, and
// nothing releases them until this elapses, so a close that returns first gave
// up on a call that was still inside. This bounds what the scenario can see: a
// close that waits for longer than this and then gives up is not distinguished
// from one that waits properly, which is the honest limit of the check.
constexpr int kHeldWindowMs = 500;

// How long the pollers are given to be counted inside the library before the
// close begins. Reaching `pollers` is the normal path; the deadline only stops
// a bridge that never counts them from hanging the scenario.
constexpr int kCountedDeadlineMs = 2000;

// One iteration of the quiesce itself, with the guesswork taken out: the pollers
// are held inside the library by the bridge's own development seam until this
// scenario lets them go, and the bridge says how many calls it counted rather
// than the scenario assuming the schedule put them there.
//
// Three promises, and a mutation that breaks any one of them is seen here:
//  - the calls are counted (`peak`), so a blocking crossing outside a call scope
//    is not merely uncounted-but-lucky, it reads as zero;
//  - they were still counted when the close began its wait (`calls_at_close`),
//    so a poll that stopped blocking cannot leave the scenario testing an empty
//    library;
//  - the close did not return while they were held, so a wait that gives up
//    after any interval shorter than the hold is not a wait.
std::string HoldCloseOnce(int pollers) {
    cultmesh_quic_debug_hold_calls(1);
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr) {
        cultmesh_quic_debug_hold_calls(0);
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);
    }

    std::atomic<int> returned{0};
    std::atomic<int> undefined_return{0};
    std::vector<std::thread> threads;
    threads.reserve(static_cast<size_t>(pollers));
    for (int index = 0; index < pollers; ++index) {
        threads.emplace_back([runtime, &returned, &undefined_return] {
            std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
            cultmesh_quic_event event{};
            int32_t required = 0;
            const int32_t result = cultmesh_quic_next_event(
                runtime, kPollTimeoutMs, &event, payload.data(), kPayloadCapacity, &required);
            if (result != 0 && result != 1 && result != 2) undefined_return.fetch_add(1);
            returned.fetch_add(1);
        });
    }

    const auto counted_deadline =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(kCountedDeadlineMs);
    while (cultmesh_quic_debug_peak_calls() < pollers &&
           std::chrono::steady_clock::now() < counted_deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    std::this_thread::sleep_for(std::chrono::milliseconds(kSettleMs));

    std::atomic<bool> closed{false};
    std::thread closer([runtime, &closed] {
        cultmesh_quic_runtime_close(runtime);
        closed.store(true);
    });
    const auto held_until =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(kHeldWindowMs);
    while (!closed.load() && std::chrono::steady_clock::now() < held_until)
        std::this_thread::sleep_for(std::chrono::milliseconds(5));

    const bool closed_while_held = closed.load();
    const int32_t peak = cultmesh_quic_debug_peak_calls();
    const int32_t at_close = cultmesh_quic_debug_calls_at_close();

    cultmesh_quic_debug_hold_calls(0);
    closer.join();
    for (auto& thread : threads) thread.join();

    if (peak != pollers)
        return "the library's peak count of host calls inside it was " + std::to_string(peak) + ", not " +
            std::to_string(pollers) + ": either the blocking crossing is not inside a call scope, or it " +
            "did not block long enough for the pollers to be inside it together";
    if (at_close != pollers)
        return "the close began its wait with " + std::to_string(at_close) +
            " host call(s) counted inside, not " + std::to_string(pollers) +
            "; the pollers were not inside the library when the close started";
    if (closed_while_held)
        return "the close returned while " + std::to_string(pollers) +
            " host call(s) were still held inside the library";
    if (undefined_return.load() != 0)
        return std::to_string(undefined_return.load()) + " poller(s) saw a return the header does not define";
    if (returned.load() != pollers)
        return std::to_string(returned.load()) + " of " + std::to_string(pollers) + " pollers returned";
    return {};
}

// One iteration of the other half of section 4: a call made while a close is
// running is refused, and is not counted behind the close's wait.
//
// The prohibition is the host's — no call may begin after the close starts — and
// the bridge does not make it optional. What it does do is refuse the call that
// races the start, and that refusal is load-bearing rather than courteous: a
// call admitted after the closer has begun increments the in-flight count behind
// a wait that has already read it, and the wait can then be left waiting on a
// count that reaches zero only when the late caller happens to leave.
//
// The seam is what makes the window wide enough to aim at: a held poller keeps
// the close inside its wait for as long as this scenario wants it there.
std::string LateCallOnce() {
    cultmesh_quic_debug_hold_calls(1);
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr) {
        cultmesh_quic_debug_hold_calls(0);
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);
    }

    std::thread poller([runtime] {
        std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
        cultmesh_quic_event event{};
        int32_t required = 0;
        cultmesh_quic_next_event(
            runtime, kPollTimeoutMs, &event, payload.data(), kPayloadCapacity, &required);
    });

    const auto counted_deadline =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(kCountedDeadlineMs);
    while (cultmesh_quic_debug_peak_calls() < 1 &&
           std::chrono::steady_clock::now() < counted_deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    std::this_thread::sleep_for(std::chrono::milliseconds(kSettleMs));

    std::thread closer([runtime] { cultmesh_quic_runtime_close(runtime); });
    // The close has begun its wait once it has read the count, and the held
    // poller is what keeps it there.
    const auto started_deadline =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(kCountedDeadlineMs);
    while (cultmesh_quic_debug_calls_at_close() < 0 &&
           std::chrono::steady_clock::now() < started_deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    const int32_t at_close = cultmesh_quic_debug_calls_at_close();

    // Two calls, from a thread of their own so that a bridge which admits them
    // cannot park this one: one that does not block, and then the blocking
    // crossing. That order, because a bridge that admits the blocking one parks
    // it inside the library until the hold is released, and a call made after
    // that would be racing the teardown rather than the wait.
    std::atomic<int32_t> late_poll{0};
    std::atomic<int32_t> late_error{0};
    std::thread late([runtime, &late_poll, &late_error] {
        std::array<char, 256> message{};
        late_error.store(cultmesh_quic_last_error(runtime, message.data(),
            static_cast<int32_t>(message.size())));
        std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
        cultmesh_quic_event event{};
        int32_t required = 0;
        late_poll.store(cultmesh_quic_next_event(
            runtime, kPollTimeoutMs, &event, payload.data(), kPayloadCapacity, &required));
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(kSettleMs));
    cultmesh_quic_debug_hold_calls(0);
    late.join();
    closer.join();
    poller.join();

    if (at_close < 1)
        return "the close did not begin its wait with the poller counted inside, so nothing held it "
            "open for a call to race";
    if (late_poll.load() != -1)
        return "cultmesh_quic_next_event returned " + std::to_string(late_poll.load()) +
            " to a call made during the close, not the -1 that refuses it";
    if (late_error.load() != -1)
        return "cultmesh_quic_last_error returned " + std::to_string(late_error.load()) +
            " to a call made during the close, not the -1 that refuses it";
    return {};
}

// How long `holdtimeout`'s two polls ask for, and how long the hold stays armed
// under them. The gap between each poll and the hold is the whole measurement:
// a poll the hold parks leaves when the hold is released, so its elapsed time
// lands at the last number instead of its own.
//
// Two polls, a short one and a long one, because a guard that tests the timeout
// instead of the wake — park it unless it was long — is identity on any single
// value on one side of its threshold. The long one sits above 1500, the stated
// ceiling on how long the hold guard may treat a long wait as surely worth
// parking; the hold stays armed well past it so a guard that parks anyway is
// held for the rest of the arming instead of leaving on its own timeout, which
// is the overshoot this scenario's late tolerance is wide enough to see.
constexpr int32_t kHeldTimeoutPollMs = 150;
constexpr int32_t kHeldLongTimeoutPollMs = 1600;
constexpr int kHoldArmedMs = 3000;

// The seam's own rule, and the only scenario that can see it: the hold parks a
// call the wait woke, and not one whose own timeout expired.
//
// Everything else that arms the hold parks a woken call, so the guard governs
// nothing either of them observes — the timeout scenario never arms the hold,
// and the hold scenarios never let a timeout expire, so nothing was ever in
// both states at once and the guard could be deleted with every scenario still
// green. This puts calls in both states: the hold is armed, and the runtime is
// idle, so each wait ends on its timeout and the call is on its way out.
//
// It matters because a scenario that parked such a call would then report the
// bridge keeping a host call inside the library when what kept it was the
// fixture — the quiesce numbers `holdclose` asserts would be the seam's, not
// the bridge's.
std::string HoldTimeoutOnce(std::array<Overshoot, 2>& overshoot) {
    cultmesh_quic_debug_hold_calls(1);
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr) {
        cultmesh_quic_debug_hold_calls(0);
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);
    }

    // Each on a thread of its own, because a parked call does not come back until
    // the release below and this thread is what releases it.
    constexpr std::array<int32_t, 2> timeouts{kHeldTimeoutPollMs, kHeldLongTimeoutPollMs};
    std::array<TimedPoll, 2> polls{};
    std::array<std::thread, 2> pollers;
    for (size_t index = 0; index < timeouts.size(); ++index)
        pollers[index] = std::thread([runtime, &polls, &timeouts, index] {
            polls[index] = PollFor(runtime, timeouts[index]);
        });

    std::this_thread::sleep_for(std::chrono::milliseconds(kHoldArmedMs));
    cultmesh_quic_debug_hold_calls(0);
    for (auto& poller : pollers) poller.join();
    cultmesh_quic_runtime_close(runtime);

    for (size_t index = 0; index < timeouts.size(); ++index) {
        const TimedPoll& poll = polls[index];
        const int32_t timeout_ms = timeouts[index];
        overshoot[index].Record(poll, timeout_ms);
        if (poll.result != 0)
            return "a poll ended by its own timeout returned " + std::to_string(poll.result) +
                ", not the 0 an idle runtime owes it";
        if (poll.elapsed_ms < timeout_ms - kEarlyToleranceMs)
            return "a poll asking for " + std::to_string(timeout_ms) + " ms returned after " +
                std::to_string(poll.elapsed_ms) + " ms, so the fixture never let its timeout run out";
        if (poll.elapsed_ms > timeout_ms + kGenerousLateToleranceMs)
            return "a poll asking for " + std::to_string(timeout_ms) + " ms returned after " +
                std::to_string(poll.elapsed_ms) + " ms: the hold parked a call its own timeout had "
                "already ended, and it left when the hold did";
    }
    return {};
}

int HoldTimeout(int iterations) {
    std::array<Overshoot, 2> overshoot{};
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = HoldTimeoutOnce(overshoot);
        if (!failure.empty())
            return Fail("holdtimeout iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("holdtimeout %dx: ok (worst overshoot %lld ms at %d, %lld ms at %d)\n", iterations,
        overshoot[0].worst_ms, kHeldTimeoutPollMs, overshoot[1].worst_ms, kHeldLongTimeoutPollMs);
    return 0;
}

int LateCall(int iterations) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = LateCallOnce();
        if (!failure.empty())
            return Fail("latecall iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("latecall %dx: ok\n", iterations);
    return 0;
}

int HoldClose(int iterations, int pollers) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = HoldCloseOnce(pollers);
        if (!failure.empty())
            return Fail("holdclose iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("holdclose %dx%d: ok\n", iterations, pollers);
    return 0;
}

// The values `waitseam` asks for: the smallest, the seam's own sentinel-versus-
// zero boundary, one on either side of a 16 ms quantum and of a 40 ms one, the
// ordinary `pollbusy` timeout and its neighbours, `polltimeout`'s long probe,
// and INT32_MAX. Nothing in section 6 of the header states a maximum on
// `timeout_ms` narrower than what `int32_t` itself holds, so INT32_MAX is the
// ceiling this checks and nothing wider is claimed.
constexpr std::array<int32_t, 11> kWaitSeamProbesMs{
    0, 1, 15, 16, 39, 40, 999, 1000, 1001, 7300, INT32_MAX};

// How long a probe's own poller thread is given to reach the wait before this
// gives up on it and fails with the probe named, rather than hanging.
constexpr int kWaitSeamDeadlineMs = 2000;

// One probe: ask a fresh, otherwise-idle runtime for `timeout_ms` and read
// `cultmesh_quic_debug_last_wait_ms` — the bridge's own account of what it
// handed its condition wait — instead of measuring how long the call took.
// Every clamp, floor, round, scale, offset and later-poll mapping Soul's seven
// passes found is the identity at some wall-clock probe; none of them is the
// identity here unless it is also the identity at every value below, because
// this reads the argument itself rather than inferring it from elapsed time.
//
// `timeout_ms <= 0` never reaches the wait at all — `cultmesh_quic_next_event`
// returns at once when the queue is empty — so there is nothing for the seam to
// record, and the honest check is the converse: it must still say nothing,
// which is exactly what a seam that recorded the raw argument on every call,
// instead of only the value it actually handed its wait, would get wrong.
std::string WaitSeamProbeOnce(int32_t timeout_ms) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);
    cultmesh_quic_debug_reset_last_wait_ms();

    std::string failure;
    if (timeout_ms <= 0) {
        std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
        cultmesh_quic_event event{};
        int32_t required = 0;
        const int32_t result = cultmesh_quic_next_event(
            runtime, timeout_ms, &event, payload.data(), kPayloadCapacity, &required);
        const int32_t recorded = cultmesh_quic_debug_last_wait_ms();
        if (result != 0)
            failure = "a poll asking for " + std::to_string(timeout_ms) + " ms returned " +
                std::to_string(result) + ", not the 0 an idle runtime owes it";
        else if (recorded != -1)
            failure = "a poll asking for " + std::to_string(timeout_ms) + " ms recorded " +
                std::to_string(recorded) + " ms on the wait seam, though the bridge's own guard "
                "never enters a wait for a timeout that is not greater than zero";
        cultmesh_quic_runtime_close(runtime);
        return failure;
    }

    // A real wait, on a thread of its own: a probe at INT32_MAX has to be
    // released by the close, not waited out.
    std::thread poller([runtime, timeout_ms] {
        std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
        cultmesh_quic_event event{};
        int32_t required = 0;
        cultmesh_quic_next_event(runtime, timeout_ms, &event, payload.data(), kPayloadCapacity, &required);
    });

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(kWaitSeamDeadlineMs);
    int32_t recorded = cultmesh_quic_debug_last_wait_ms();
    while (recorded == -1 && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        recorded = cultmesh_quic_debug_last_wait_ms();
    }

    cultmesh_quic_runtime_close(runtime);
    poller.join();

    if (recorded != timeout_ms)
        failure = "a poll asking for " + std::to_string(timeout_ms) + " ms recorded " +
            std::to_string(recorded) + " ms on the wait seam: the bridge handed its condition "
            "wait something other than the host's own argument";
    return failure;
}

// Past the twelfth poll on one runtime: no scenario before this one ever asked
// a runtime for a thirteenth, so a wait that only changes shape after twelve
// had nowhere to be seen. Forty short waits on a runtime that stays open for
// all of them, each checked by equality.
constexpr int32_t kWaitSeamRepeatedMs = 5;
constexpr int kWaitSeamRepeatedPolls = 40;

std::string WaitSeamRepeatedOnce() {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    std::string failure;
    for (int poll = 0; poll < kWaitSeamRepeatedPolls && failure.empty(); ++poll) {
        cultmesh_quic_debug_reset_last_wait_ms();
        std::vector<uint8_t> payload(static_cast<size_t>(kPayloadCapacity));
        cultmesh_quic_event event{};
        int32_t required = 0;
        cultmesh_quic_next_event(
            runtime, kWaitSeamRepeatedMs, &event, payload.data(), kPayloadCapacity, &required);
        const int32_t recorded = cultmesh_quic_debug_last_wait_ms();
        if (recorded != kWaitSeamRepeatedMs)
            failure = "poll " + std::to_string(poll + 1) + " of " +
                std::to_string(kWaitSeamRepeatedPolls) + " on one runtime recorded " +
                std::to_string(recorded) + " ms, not " + std::to_string(kWaitSeamRepeatedMs);
    }
    cultmesh_quic_runtime_close(runtime);
    return failure;
}

int WaitSeam(int iterations) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        for (int32_t probe : kWaitSeamProbesMs) {
            const std::string failure = WaitSeamProbeOnce(probe);
            if (!failure.empty())
                return Fail("waitseam iteration " + std::to_string(iteration) + " probe " +
                    std::to_string(probe) + ": " + failure);
        }
        const std::string repeated = WaitSeamRepeatedOnce();
        if (!repeated.empty())
            return Fail("waitseam iteration " + std::to_string(iteration) + ": " + repeated);
    }
    std::printf("waitseam %dx: ok (%zu probes, %d repeated polls on one runtime)\n",
        iterations, kWaitSeamProbesMs.size(), kWaitSeamRepeatedPolls);
    return 0;
}

#endif  // CULTMESH_QUIC_DEBUG_ASSERTS

// How long `pollhammer`'s idle poll asks for. One host thread hammers every
// gate-touching entry point that cannot itself queue an event, in a yield
// loop, while another holds this one poll idle; each hammered call's
// `CallScope` destructor notifies every waiter on the runtime's condition
// variable (cultmesh_quic_native.cpp:286), so the idle poll is woken many times
// over for every timeout it is given here. That is exactly the shape a wake
// count or an error-state predicate exploits, and exactly what a revert that
// returns on any wake at all fails immediately.
//
// `cultmesh_quic_connection_open`, `cultmesh_quic_listener_open` and
// `cultmesh_quic_stream_open` touch the same gate but can each queue a real
// event on this runtime, and a genuine event is allowed to end the idle poll
// early — hammering with those would make an early return ambiguous between
// the fault this scenario hunts and a real delivery, so they are left out. The
// refused `listener_open` below is a setup step run once, not part of the
// loop, and it leaves no listener behind.
constexpr int32_t kHammerPollMs = 1000;

// A self-signed ECDSA P-256 PKCS12, generated once with OpenSSL and embedded so
// this scenario needs no crypto library of its own and no network fetch:
//   openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
//     -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=cultmesh-quic-native-tests"
//   openssl pkcs12 -export -out test.p12 -inkey key.pem -in cert.pem -passout pass:
// Used only to give pollhammer's live-objects run a listener that can actually
// load a credential; nothing ever connects to it, so it never accepts and never
// queues an event this scenario would have to tell apart from a bug.
constexpr std::array<uint8_t, 1088> kSelfSignedPkcs12{{
    0x30, 0x82, 0x04, 0x3c, 0x02, 0x01, 0x03, 0x30, 0x82, 0x03, 0xf2, 0x06,
    0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x07, 0x01, 0xa0, 0x82,
    0x03, 0xe3, 0x04, 0x82, 0x03, 0xdf, 0x30, 0x82, 0x03, 0xdb, 0x30, 0x82,
    0x02, 0x8a, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x07,
    0x06, 0xa0, 0x82, 0x02, 0x7b, 0x30, 0x82, 0x02, 0x77, 0x02, 0x01, 0x00,
    0x30, 0x82, 0x02, 0x70, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d,
    0x01, 0x07, 0x01, 0x30, 0x5f, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7,
    0x0d, 0x01, 0x05, 0x0d, 0x30, 0x52, 0x30, 0x31, 0x06, 0x09, 0x2a, 0x86,
    0x48, 0x86, 0xf7, 0x0d, 0x01, 0x05, 0x0c, 0x30, 0x24, 0x04, 0x10, 0x48,
    0xdf, 0x04, 0x5a, 0x31, 0x34, 0x6d, 0xe0, 0x7c, 0xde, 0x3c, 0xa2, 0xde,
    0xf2, 0x8d, 0x20, 0x02, 0x02, 0x08, 0x00, 0x30, 0x0c, 0x06, 0x08, 0x2a,
    0x86, 0x48, 0x86, 0xf7, 0x0d, 0x02, 0x09, 0x05, 0x00, 0x30, 0x1d, 0x06,
    0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x01, 0x2a, 0x04, 0x10,
    0x44, 0x04, 0xd9, 0x9b, 0x99, 0x3a, 0xcb, 0x89, 0xc0, 0xcb, 0x53, 0xe3,
    0xe3, 0x5b, 0xb4, 0x48, 0x80, 0x82, 0x02, 0x00, 0x92, 0xcc, 0x04, 0xcf,
    0x7d, 0x58, 0xec, 0x68, 0x1a, 0x1b, 0xd5, 0x3b, 0x17, 0x57, 0x22, 0xd5,
    0x45, 0x00, 0xff, 0x05, 0x0f, 0x0d, 0x18, 0x8b, 0x72, 0x5b, 0xcc, 0xd9,
    0x87, 0xf4, 0x99, 0x44, 0x7c, 0xd6, 0xc3, 0x02, 0x2a, 0xaf, 0xd9, 0x10,
    0x14, 0xd3, 0x9f, 0x1a, 0x2c, 0x7b, 0x4f, 0xe6, 0x0b, 0x1d, 0x7f, 0xb9,
    0xf4, 0x01, 0x5c, 0x4b, 0xd5, 0xa1, 0x57, 0xb0, 0x3d, 0xe0, 0xbc, 0x70,
    0x72, 0xe0, 0xe9, 0xb8, 0x9a, 0xde, 0x68, 0xfe, 0x7c, 0xc7, 0xca, 0xcc,
    0x61, 0x32, 0x88, 0x72, 0xdd, 0x97, 0x28, 0x7b, 0x5b, 0x0d, 0x30, 0xf5,
    0xa2, 0xd1, 0xe8, 0x89, 0xd4, 0x1d, 0x88, 0x28, 0xd2, 0xc3, 0x02, 0x93,
    0xa1, 0x27, 0xfb, 0x4a, 0xe7, 0xdc, 0xac, 0xf3, 0x0d, 0xa9, 0xd3, 0x14,
    0x37, 0x71, 0xb7, 0xe5, 0xaf, 0xdb, 0xb6, 0xda, 0x42, 0xb7, 0x24, 0x7a,
    0x94, 0xad, 0x73, 0x79, 0xde, 0x21, 0xb9, 0x13, 0x63, 0x98, 0x33, 0x62,
    0x4a, 0x36, 0xb8, 0x6d, 0x14, 0x8b, 0x84, 0xf0, 0x8e, 0x55, 0xcc, 0x95,
    0x14, 0x54, 0xcc, 0x68, 0x4b, 0x09, 0x24, 0xb4, 0x79, 0x49, 0x05, 0xc7,
    0x0e, 0xfe, 0xed, 0x23, 0x8c, 0x87, 0xc6, 0xc3, 0x76, 0x25, 0xd9, 0xd6,
    0xae, 0x5a, 0xf4, 0x86, 0x74, 0xb8, 0x44, 0x22, 0xe8, 0xfc, 0xa5, 0xc2,
    0x25, 0xbd, 0x84, 0xfd, 0x92, 0x96, 0xe2, 0x66, 0xa9, 0xb0, 0x1f, 0xd3,
    0x8d, 0xf7, 0x1c, 0xce, 0xed, 0x09, 0x11, 0x61, 0xec, 0x2d, 0xa4, 0x9d,
    0xb9, 0xdb, 0xd4, 0x2e, 0xa4, 0xff, 0xe6, 0x09, 0x8e, 0x9a, 0x0b, 0xdf,
    0x30, 0xf4, 0xea, 0xe6, 0x09, 0x2d, 0x16, 0xec, 0x5d, 0xb6, 0x39, 0xda,
    0xd4, 0x43, 0x41, 0x95, 0xc8, 0x32, 0xd2, 0xac, 0x17, 0x58, 0x05, 0x7e,
    0x10, 0x24, 0xae, 0xd6, 0x58, 0x80, 0x0a, 0x29, 0xcb, 0x4b, 0x78, 0x62,
    0xbe, 0xf6, 0x6c, 0x7d, 0x44, 0x07, 0x7b, 0xfb, 0xd9, 0xb6, 0x74, 0x75,
    0x9f, 0xe6, 0xb4, 0xc3, 0x54, 0x53, 0x46, 0xdb, 0xf2, 0xa1, 0x47, 0xb8,
    0xf1, 0xff, 0x01, 0xba, 0xde, 0xf9, 0xca, 0xd6, 0xc0, 0x38, 0x40, 0xd0,
    0x86, 0x72, 0xc8, 0xcf, 0xdd, 0xd6, 0xf6, 0x36, 0xe0, 0xf3, 0x2e, 0x95,
    0x23, 0xdb, 0x99, 0x72, 0x1c, 0xa6, 0xa0, 0xf7, 0x17, 0x93, 0x1e, 0x20,
    0x6c, 0x75, 0x25, 0x0e, 0x1c, 0xab, 0xa4, 0xce, 0x35, 0xd4, 0xf4, 0xc6,
    0x63, 0x89, 0x0d, 0xa9, 0x91, 0x0b, 0x7e, 0xce, 0x7d, 0x4d, 0x5e, 0xbf,
    0xfd, 0xa9, 0xbd, 0xb6, 0x1e, 0x2b, 0x08, 0x5d, 0x7a, 0xdd, 0x61, 0xdf,
    0x24, 0xe0, 0xff, 0x4b, 0xeb, 0xa2, 0xeb, 0xb6, 0x2f, 0x2c, 0xbf, 0x9b,
    0x5f, 0x6a, 0x1a, 0x33, 0xae, 0x02, 0x6d, 0xbf, 0x8e, 0x6f, 0xeb, 0xda,
    0x17, 0x88, 0x79, 0xf8, 0xe0, 0x1c, 0x1d, 0x64, 0x7c, 0xc3, 0xc2, 0xc1,
    0xce, 0x12, 0x67, 0x74, 0xbc, 0xef, 0x9f, 0x2f, 0x35, 0xef, 0x40, 0x24,
    0xc1, 0x5f, 0x82, 0xc9, 0x00, 0x2c, 0xbf, 0x2b, 0xbf, 0x64, 0x39, 0x63,
    0x0c, 0xcd, 0x6f, 0xe5, 0x18, 0xfa, 0xeb, 0x02, 0x61, 0xc3, 0x22, 0xb0,
    0xfd, 0xfb, 0x9d, 0x32, 0xbb, 0x2f, 0xd8, 0x9d, 0x63, 0x8d, 0x9e, 0xb1,
    0x5a, 0x7d, 0xc3, 0xe5, 0xcc, 0xae, 0xe4, 0x71, 0x0d, 0x7b, 0xa9, 0x80,
    0xbd, 0xef, 0x86, 0x12, 0x13, 0x42, 0xf8, 0x14, 0xcb, 0xa9, 0xfd, 0x86,
    0xea, 0xd5, 0x81, 0x54, 0x87, 0xbc, 0x66, 0x24, 0xfd, 0x12, 0xf5, 0x6b,
    0xf4, 0x45, 0x7e, 0x41, 0x8e, 0x00, 0xf3, 0x63, 0xc7, 0x24, 0xc5, 0xa1,
    0xfd, 0x70, 0xbc, 0xfa, 0x26, 0xa0, 0x07, 0xc2, 0x97, 0xb1, 0xe0, 0x42,
    0x5a, 0x2b, 0x65, 0xec, 0xda, 0x45, 0x79, 0xe5, 0x18, 0xf2, 0xce, 0x8e,
    0xc3, 0xe3, 0x74, 0x6d, 0x30, 0x82, 0x01, 0x49, 0x06, 0x09, 0x2a, 0x86,
    0x48, 0x86, 0xf7, 0x0d, 0x01, 0x07, 0x01, 0xa0, 0x82, 0x01, 0x3a, 0x04,
    0x82, 0x01, 0x36, 0x30, 0x82, 0x01, 0x32, 0x30, 0x82, 0x01, 0x2e, 0x06,
    0x0b, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x0c, 0x0a, 0x01, 0x02,
    0xa0, 0x81, 0xf7, 0x30, 0x81, 0xf4, 0x30, 0x5f, 0x06, 0x09, 0x2a, 0x86,
    0x48, 0x86, 0xf7, 0x0d, 0x01, 0x05, 0x0d, 0x30, 0x52, 0x30, 0x31, 0x06,
    0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x05, 0x0c, 0x30, 0x24,
    0x04, 0x10, 0xd8, 0x91, 0xb9, 0x92, 0x3a, 0x1e, 0x6b, 0xa2, 0x57, 0x44,
    0x16, 0x3a, 0xea, 0x36, 0x8d, 0x60, 0x02, 0x02, 0x08, 0x00, 0x30, 0x0c,
    0x06, 0x08, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x02, 0x09, 0x05, 0x00,
    0x30, 0x1d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x01,
    0x2a, 0x04, 0x10, 0x35, 0x48, 0x03, 0x23, 0x6a, 0x3d, 0x5f, 0x96, 0xb7,
    0xfb, 0xb1, 0xcb, 0xdc, 0x0c, 0x1f, 0x79, 0x04, 0x81, 0x90, 0x4d, 0x36,
    0x5b, 0x1e, 0xc1, 0xc1, 0xa3, 0xfa, 0x85, 0xe6, 0x58, 0x89, 0x5d, 0xbe,
    0x17, 0xb4, 0x90, 0x96, 0x2d, 0x3d, 0x32, 0xe2, 0x73, 0x6c, 0x2c, 0xfd,
    0x7b, 0xe4, 0x9c, 0x82, 0x3c, 0xe4, 0x7a, 0x58, 0x45, 0xdd, 0xcd, 0x00,
    0xa2, 0x52, 0xf9, 0x98, 0xd4, 0x0f, 0x64, 0x0b, 0x58, 0x27, 0xfe, 0xfc,
    0xfd, 0x60, 0x3f, 0x7e, 0x6f, 0x73, 0x73, 0xda, 0x16, 0x34, 0x83, 0x38,
    0x89, 0x3d, 0xbe, 0xde, 0x0a, 0x47, 0x7b, 0xcd, 0xc1, 0x88, 0x72, 0x38,
    0x95, 0xb5, 0x54, 0xfa, 0x2a, 0x84, 0x2b, 0xcd, 0x98, 0xe3, 0x20, 0xd2,
    0xac, 0xd8, 0xf5, 0xa8, 0xd2, 0xef, 0x1f, 0x99, 0x88, 0x16, 0x6f, 0xf2,
    0x08, 0xe9, 0x28, 0xc8, 0x7a, 0x02, 0x57, 0x5c, 0x1c, 0x94, 0x0f, 0x57,
    0xfc, 0x0e, 0x85, 0x63, 0xc5, 0x44, 0x45, 0x4f, 0xc6, 0x35, 0x52, 0x3d,
    0xd4, 0xe2, 0x22, 0x6f, 0x72, 0xde, 0x55, 0xad, 0x20, 0x84, 0x91, 0x14,
    0x7e, 0x5f, 0x98, 0x2a, 0x5e, 0x2b, 0xf4, 0xc5, 0x10, 0x1b, 0x31, 0x25,
    0x30, 0x23, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x09,
    0x15, 0x31, 0x16, 0x04, 0x14, 0xbd, 0x8a, 0x08, 0x3c, 0x66, 0xf7, 0x9d,
    0xa0, 0x25, 0x8b, 0x33, 0xac, 0xc2, 0x5e, 0xdc, 0x76, 0xd4, 0x33, 0x1a,
    0xd6, 0x30, 0x41, 0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48,
    0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20, 0x4e, 0x6f,
    0x75, 0x90, 0x57, 0xb4, 0x39, 0x1e, 0xc3, 0x97, 0x50, 0x5e, 0x4d, 0xf2,
    0xa8, 0xf9, 0x0e, 0xcd, 0x7f, 0x57, 0x31, 0xae, 0xf2, 0x87, 0x0a, 0x55,
    0xb0, 0xbc, 0xd6, 0x11, 0x83, 0xc3, 0x04, 0x08, 0x52, 0x79, 0xb1, 0x83,
    0xe6, 0xe4, 0x6e, 0x4d, 0x02, 0x02, 0x08, 0x00,
}};

std::string PollHammerOnce(bool with_recorded_error, bool with_live_objects) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    if (with_recorded_error) {
        // Not a certificate at all: PKCS12 parsing fails before any network
        // I/O, so this is refused synchronously and leaves no listener behind,
        // and `runtime->error` is non-empty for the rest of this runtime's life.
        const uint8_t junk[] = {0x00, 0x01, 0x02, 0x03};
        uint64_t listener_id = 0;
        uint16_t bound_port = 0;
        const int32_t result = cultmesh_quic_listener_open(
            runtime, nullptr, 0, junk, static_cast<int32_t>(sizeof(junk)), "", &listener_id, &bound_port);
        if (result >= 0) {
            cultmesh_quic_runtime_close(runtime);
            return "cultmesh_quic_listener_open accepted a junk PKCS12 credential";
        }
    }

    // Self's ruling of 2026-09-22, F3: the predicate covered by the hammer set
    // above is only ever exercised on a runtime with nothing live in it. An
    // unroutable address (RFC 5737 TEST-NET-1) gets a connection into
    // `runtime->connections` at once — `cultmesh_quic_connection_open` inserts
    // it before the handshake starts — and that handshake then never completes
    // inside this scenario's poll window, so it queues no event of its own. A
    // best-effort stream rides the same connection. The listener is real (a
    // credential MsQuic actually loads) but nothing ever connects to it, so it
    // never accepts and never queues an event either: every object here is
    // live in the runtime's maps without ever waking the idle poll on its own.
    uint64_t live_connection_id = 0;
    uint64_t live_stream_id = 0;
    uint64_t live_listener_id = 0;
    if (with_live_objects) {
        const int32_t connecting =
            cultmesh_quic_connection_open(runtime, "192.0.2.1", 9, &live_connection_id);
        if (connecting != 0) {
            cultmesh_quic_runtime_close(runtime);
            return "cultmesh_quic_connection_open (live-objects setup) returned " +
                std::to_string(connecting);
        }
        // Best-effort: whether this succeeds is not the point of the
        // scenario, and P1's predicate names `connections`, not `streams`.
        cultmesh_quic_stream_open(
            runtime, live_connection_id, CULTMESH_QUIC_STREAM_RELIABLE, &live_stream_id);
        uint16_t bound_port = 0;
        cultmesh_quic_listener_open(runtime, "127.0.0.1", 0, kSelfSignedPkcs12.data(),
            static_cast<int32_t>(kSelfSignedPkcs12.size()), "", &live_listener_id, &bound_port);
    }

    std::atomic<bool> hammering{true};
    std::thread hammer([runtime, &hammering] {
        constexpr uint64_t kBogusId = 0xffffffffffffffffull;
        const uint8_t frame[] = {0x00};
        std::array<char, 256> message{};
        while (hammering.load(std::memory_order_relaxed)) {
            cultmesh_quic_last_error(runtime, message.data(), static_cast<int32_t>(message.size()));
            cultmesh_quic_last_status(runtime);
            cultmesh_quic_connection_shutdown(runtime, kBogusId, 0);
            cultmesh_quic_stream_shutdown(runtime, kBogusId, 0);
            cultmesh_quic_stream_send_frame(runtime, kBogusId, frame, 1, 0);
            cultmesh_quic_connection_certificate_complete(runtime, kBogusId, 0);
            cultmesh_quic_listener_close(runtime, kBogusId);
            std::this_thread::yield();
        }
    });

    const TimedPoll poll = PollFor(runtime, kHammerPollMs);
    hammering.store(false, std::memory_order_relaxed);
    hammer.join();
    if (live_listener_id != 0) cultmesh_quic_listener_close(runtime, live_listener_id);
    if (live_connection_id != 0) cultmesh_quic_connection_shutdown(runtime, live_connection_id, 0);
    (void)live_stream_id;
    cultmesh_quic_runtime_close(runtime);

    if (poll.result != 0)
        return "the idle poll returned " + std::to_string(poll.result) +
            ", not the 0 an idle runtime owes it";
    if (poll.elapsed_ms < kHammerPollMs - kEarlyToleranceMs)
        return "the idle poll returned after " + std::to_string(poll.elapsed_ms) +
            " ms while another thread hammered the gate, ending its wait early";
    if (poll.elapsed_ms > static_cast<long long>(kHammerPollMs) + kGenerousLateToleranceMs)
        return "the idle poll returned after " + std::to_string(poll.elapsed_ms) +
            " ms: well past its own timeout";
    return {};
}

int PollHammer(int iterations) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        std::string failure = PollHammerOnce(false, false);
        if (!failure.empty())
            return Fail("pollhammer iteration " + std::to_string(iteration) + " (clean runtime): " + failure);
        failure = PollHammerOnce(true, false);
        if (!failure.empty())
            return Fail("pollhammer iteration " + std::to_string(iteration) +
                " (after a refused call recorded an error): " + failure);
        failure = PollHammerOnce(false, true);
        if (!failure.empty())
            return Fail("pollhammer iteration " + std::to_string(iteration) +
                " (with a live connection, stream and listener): " + failure);
    }
    std::printf("pollhammer %dx: ok\n", iterations);
    return 0;
}

// Self's ruling of 2026-09-22, F4: any `timeout_ms <= 0` has to return within a
// small, generously-margined bound, and a negative timeout has to be probed
// too — the bridge's own guard (`timeout_ms > 0`) already treats zero and
// negative alike, so 0, -1 and INT32_MIN all take the same fast path and none
// of them may collide with the seam's own "nothing recorded" sentinel.
constexpr std::array<int32_t, 3> kZeroTimeoutProbesMs{0, -1, INT32_MIN};

// Generous next to `kEarlyToleranceMs`/`kGenerousLateToleranceMs` because a
// non-positive timeout never enters the condition wait at all, so this is
// bounding a runtime lock acquisition and an empty-queue check, not a
// scheduler wakeup — the margin only has to be well clear of a mistake that
// makes this path sleep or spin, not of ordinary jitter.
constexpr int kZeroTimeoutBoundMs = 50;

std::string ZeroTimeoutOnce(int32_t timeout_ms) {
    void* runtime = nullptr;
    const int32_t opened = cultmesh_quic_runtime_open("cultmesh-quic-native-tests", &runtime);
    if (opened != 0 || runtime == nullptr)
        return "cultmesh_quic_runtime_open returned " + std::to_string(opened);

    const TimedPoll poll = PollFor(runtime, timeout_ms);
    cultmesh_quic_runtime_close(runtime);

    if (poll.result != 0)
        return "a poll asking for " + std::to_string(timeout_ms) + " ms returned " +
            std::to_string(poll.result) + ", not the 0 an idle runtime owes it";
    if (poll.elapsed_ms > kZeroTimeoutBoundMs)
        return "a poll asking for " + std::to_string(timeout_ms) + " ms returned after " +
            std::to_string(poll.elapsed_ms) + " ms: a non-positive timeout must return within " +
            std::to_string(kZeroTimeoutBoundMs) + " ms, not stall or spin";
    return {};
}

int ZeroTimeout(int iterations) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        for (int32_t probe : kZeroTimeoutProbesMs) {
            const std::string failure = ZeroTimeoutOnce(probe);
            if (!failure.empty())
                return Fail("zerotimeout iteration " + std::to_string(iteration) + " probe " +
                    std::to_string(probe) + ": " + failure);
        }
    }
    std::printf("zerotimeout %dx: ok (%zu probes)\n", iterations, kZeroTimeoutProbesMs.size());
    return 0;
}

int CloseRace(int iterations, int pollers) {
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = CloseRaceOnce(pollers);
        if (!failure.empty())
            return Fail("closerace iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("closerace %dx%d: ok\n", iterations, pollers);
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr,
            "usage: %s closerace|holdclose|holdtimeout|polltimeout|pollbusy|pollhammer|zerotimeout|"
            "payloadfit|latecall|waitseam [iterations] [pollers]\n",
            argv[0]);
        return 2;
    }
    const std::string scenario = argv[1];
    const int iterations = argc > 2 ? std::atoi(argv[2]) : 20;
    const int pollers = argc > 3 ? std::atoi(argv[3]) : 256;
    if (iterations <= 0 || pollers <= 0) {
        std::fprintf(stderr, "%s needs a positive iteration and poller count\n", scenario.c_str());
        return 2;
    }
    if (scenario == "closerace") return CloseRace(iterations, pollers);
    if (scenario == "polltimeout") return PollTimeout(iterations);
    if (scenario == "pollbusy") return PollBusy(iterations);
    if (scenario == "pollhammer") return PollHammer(iterations);
    if (scenario == "zerotimeout") return ZeroTimeout(iterations);
    if (scenario == "payloadfit") return PayloadFit(iterations);
    if (scenario == "holdclose" || scenario == "latecall" || scenario == "holdtimeout" ||
        scenario == "waitseam") {
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
        if (scenario == "latecall") return LateCall(iterations);
        if (scenario == "holdtimeout") return HoldTimeout(iterations);
        if (scenario == "waitseam") return WaitSeam(iterations);
        return HoldClose(iterations, pollers);
#else
        std::fprintf(stderr,
            "%s needs the development seam: configure with "
            "-DCULTMESH_QUIC_DEBUG_ASSERTS=ON\n", scenario.c_str());
        return 2;
#endif
    }
    std::fprintf(stderr, "unknown scenario '%s'\n", scenario.c_str());
    return 2;
}
