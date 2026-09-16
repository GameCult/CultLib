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
//
// `closerace` hunts the race: many pollers, many iterations, and whatever the
// schedule gives. `holdclose` takes the guessing out of it — the bridge's
// development seam (section 7 of the header) holds the pollers inside the
// library until this scenario releases them, and reports how many host calls the
// bridge actually counted, so the scenario asserts the quiesce instead of
// assuming its own fixture worked. It needs CULTMESH_QUIC_DEBUG_ASSERTS.
//
// Exit code 0 means every assertion held. Anything else, including a sanitizer
// abort or the bridge's own assertion, is a failure. Under ThreadSanitizer run
// it with `TSAN_OPTIONS=halt_on_error=1`, or a reported race leaves the exit
// code to the last thing that set it.

#include <cultmesh_quic_native.h>

#include <atomic>
#include <chrono>
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
        return "the library counted a peak of " + std::to_string(peak) + " host call(s) inside it, not " +
            std::to_string(pollers) + "; the blocking crossing is not inside a call scope";
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
        std::fprintf(stderr, "usage: %s closerace|holdclose [iterations] [pollers]\n", argv[0]);
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
    if (scenario == "holdclose") {
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
        return HoldClose(iterations, pollers);
#else
        std::fprintf(stderr,
            "holdclose needs the development seam: configure with "
            "-DCULTMESH_QUIC_DEBUG_ASSERTS=ON\n");
        return 2;
#endif
    }
    std::fprintf(stderr, "unknown scenario '%s'\n", scenario.c_str());
    return 2;
}
