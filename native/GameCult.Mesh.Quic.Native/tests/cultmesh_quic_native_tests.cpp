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
// Exit code 0 means every assertion held. Anything else, including a sanitizer
// abort or the bridge's own assertion, is a failure. Under ThreadSanitizer run
// it with `TSAN_OPTIONS=halt_on_error=1`, or a reported race leaves the exit
// code to the last thing that set it.

#include <cultmesh_quic_native.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

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
    // Settle so the pollers are inside the blocking wait rather than on their way
    // to it. A poller that has not entered yet is a call beginning during the
    // close, which is the host's to avoid, not the bridge's to survive.
    std::this_thread::sleep_for(std::chrono::milliseconds(50));

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

// The three timeouts `polltimeout` asks for. More than one, because a single
// value cannot tell a bridge that honours the host's timeout from one that waits
// on that same number of its own: a constant has to satisfy every band, and no
// constant is in two of them.
//
// A constant was never the hard case, though, and the numbers are chosen for the
// waits that are computed from the argument instead. Any mapping that is the
// identity at every probe passes for free, so each probe is placed to leave one
// family of ordinary mappings nowhere to be identity:
//
//  - 15 ms is as small as the OS timer lets a measurement be honest about. A
//    floor — `max(timeout, 100)`, so a polling host cannot spin — and an added
//    constant — `timeout + 100` of grace — are both the whole of the wait here,
//    and nothing larger can see either one under the scheduler's noise.
//  - 200 is the ordinary host poll, and sits between the other two so that a
//    constant cannot satisfy both of its neighbours.
//  - 7300 is long and deliberately not round. A clamp — `min(timeout, 1000)` to
//    notice a shutdown, `min(timeout, 5000)` because five seconds is surely
//    enough — is identity at any probe at or under its ceiling, and a round
//    probe is exactly where somebody's round ceiling sits.
constexpr int32_t kShortestPollMs = 15;
constexpr int32_t kShortPollMs = 200;
constexpr int32_t kLongPollMs = 7300;

// How much sooner than the timeout a poll may return. A wait may be late; it may
// not be early, because returning early is exactly what waiting on a shorter
// duration looks like. This is scheduler granularity, not slack.
constexpr int kEarlyToleranceMs = 20;

// How much later than the timeout a single poll may return: an allowance for
// the scheduler, plus a sixteenth of what was asked for.
//
// This is what the probes that look for gross failures use: a constant of the
// bridge's own, a clamp, a hold that parks a call until it is released, a wait
// that restarts on every wake. Each of those is wrong by far more than this.
constexpr int kSchedulerSlackMs = 60;
constexpr int LateToleranceMs(int32_t timeout_ms) { return kSchedulerSlackMs + timeout_ms / 16; }

// The 15 ms probe is held to something much tighter, because it is the only
// place a floor or an added constant shows and a scheduler allowance would hide
// both. Scheduler delay only ever adds, so the probe is asked
// `kShortestAttempts` times and the fastest is what is checked: noise has to
// delay every attempt to fail the bridge, and a floor or offset delays every
// attempt by construction. Every attempt is still checked for being early.
constexpr int kShortestAttempts = 10;
constexpr int kTimerSlackMs = 30;

// Both allowances were checked against a loaded machine, two busy threads per
// logical CPU for five rounds of all three timed scenarios, and not a quiet one.
// Worst overshoot seen, win32-x64 and linux-x64 on 8 logical CPUs:
//  - a single poll: 15 and 41 ms;
//  - the fastest of the 15 ms attempts: 0 and 0 ms.
// On win32-x64 that needs `MeasureAboveTheLoad` below. At normal priority under
// the same load a single poll landed up to 127 ms late and the fastest of ten
// 15 ms attempts up to 87 ms late — Windows makes a woken thread wait out busy
// threads' time slices — and a tolerance of 45 ms plus a sixteenth failed the
// unmutated bridge in four rounds of five.
//
// The honest limit. Each of these was compiled into the bridge and run through
// all three timed scenarios on both targets; what survives is not claimed:
//  - an added constant: `timeout + 25` survived and `timeout + 40` died, on the
//    15 ms probe alone;
//  - a floor: `max(timeout, 40)` survived and `max(timeout, 50)` died, on the
//    15 ms probe alone and on linux-x64 by 5 ms, so a floor near 50 is a kill
//    that rests on timing;
//  - a scaling: `timeout * 106 / 100` survived and `timeout * 108 / 100` died,
//    on the 7300 ms probe alone;
//  - a clamp: `min(timeout, 7285)` survived and `min(timeout, 7275)` died; a
//    clamp at or above 7300 is the identity at every probe;
//  - a shortening of up to `kEarlyToleranceMs` at any probe.
// Each run prints its worst overshoot per probe, so the allowances can be
// rechecked on another machine rather than trusted.

// The timed scenarios measure the bridge's wait, and on a loaded Windows machine
// what they would otherwise measure is the scheduler's: a thread woken at the
// end of its wait queues behind busy threads of equal priority for whole time
// slices. Raising this process above them takes that out of the measurement,
// and it needs no privilege on Windows. Linux schedules a woken thread promptly
// at normal priority under the same load, so nothing is asked of it there.
void MeasureAboveTheLoad() {
#if defined(_WIN32)
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
#endif
}

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
    // The fastest of several, each checked for being early; see kShortestAttempts.
    TimedPoll fastest{0, LLONG_MAX};
    for (int attempt = 0; attempt < kShortestAttempts && failure.empty(); ++attempt) {
        const TimedPoll poll = PollFor(runtime, kShortestPollMs);
        failure = CheckTimedPoll(poll, kShortestPollMs, INT_MAX);
        if (poll.elapsed_ms < fastest.elapsed_ms) fastest = poll;
    }
    if (failure.empty()) {
        overshoot[0].Record(fastest, kShortestPollMs);
        failure = CheckTimedPoll(fastest, kShortestPollMs, kTimerSlackMs);
    }
    for (size_t probe = 1; probe < kTimedProbesMs.size() && failure.empty(); ++probe) {
        const TimedPoll poll = PollFor(runtime, kTimedProbesMs[probe]);
        overshoot[probe].Record(poll, kTimedProbesMs[probe]);
        failure = CheckTimedPoll(poll, kTimedProbesMs[probe], LateToleranceMs(kTimedProbesMs[probe]));
    }

    cultmesh_quic_runtime_close(runtime);
    return failure;
}

int PollTimeout(int iterations) {
    MeasureAboveTheLoad();
    std::array<Overshoot, 3> overshoot{};
    for (int iteration = 0; iteration < iterations; ++iteration) {
        const std::string failure = PollTimeoutOnce(overshoot);
        if (!failure.empty())
            return Fail("polltimeout iteration " + std::to_string(iteration) + ": " + failure);
    }
    std::printf("polltimeout %dx: ok (worst overshoot %lld ms at %d best of %d, %lld ms at %d, %lld ms at %d)\n",
        iterations, overshoot[0].worst_ms, kTimedProbesMs[0], kShortestAttempts, overshoot[1].worst_ms, kTimedProbesMs[1],
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
    const std::string failure = CheckTimedPoll(poll, kBusyPollMs, LateToleranceMs(kBusyPollMs));
    if (!failure.empty()) return failure + ", while the host's other thread was calling in";
    // A poll that passed with fewer calls than this was never woken, and passed
    // because the fixture did nothing rather than because the bridge held.
    if (calls_during < 3)
        return "the host's other thread made " + std::to_string(calls_during) +
            " call(s) during the poll, so nothing woke it";
    return {};
}

int PollBusy(int iterations) {
    MeasureAboveTheLoad();
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
// instead of the wake — park it unless it was short — is identity on any single
// value on one side of its threshold. The long one sits above the thresholds
// such a guard would plausibly carry.
constexpr int32_t kHeldTimeoutPollMs = 150;
constexpr int32_t kHeldLongTimeoutPollMs = 1300;
constexpr int kHoldArmedMs = 2200;

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
        if (poll.elapsed_ms > timeout_ms + LateToleranceMs(timeout_ms))
            return "a poll asking for " + std::to_string(timeout_ms) + " ms returned after " +
                std::to_string(poll.elapsed_ms) + " ms: the hold parked a call its own timeout had "
                "already ended, and it left when the hold did";
    }
    return {};
}

int HoldTimeout(int iterations) {
    MeasureAboveTheLoad();
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

#endif  // CULTMESH_QUIC_DEBUG_ASSERTS

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
            "usage: %s closerace|holdclose|holdtimeout|polltimeout|pollbusy|payloadfit|latecall "
            "[iterations] [pollers]\n",
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
    if (scenario == "payloadfit") return PayloadFit(iterations);
    if (scenario == "holdclose" || scenario == "latecall" || scenario == "holdtimeout") {
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
        if (scenario == "latecall") return LateCall(iterations);
        if (scenario == "holdtimeout") return HoldTimeout(iterations);
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
