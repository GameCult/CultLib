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

    std::vector<std::thread> threads;
    threads.reserve(static_cast<size_t>(pollers));
    for (int index = 0; index < pollers; ++index) {
        threads.emplace_back([runtime, &entering, &returned, &undefined_return, &timed_out] {
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
            returned.fetch_add(1);
        });
    }

    while (entering.load() < pollers) std::this_thread::yield();
    // Settle so the pollers are inside the blocking wait rather than on their way
    // to it. A poller that has not entered yet is a call beginning during the
    // close, which is the host's to avoid, not the bridge's to survive.
    std::this_thread::sleep_for(std::chrono::milliseconds(50));

    cultmesh_quic_runtime_close(runtime);
    for (auto& thread : threads) thread.join();

    if (undefined_return.load() != 0)
        return std::to_string(undefined_return.load()) + " poller(s) saw a return the header does not define";
    if (timed_out.load() != 0)
        return std::to_string(timed_out.load()) + " of " + std::to_string(pollers) +
            " pollers sat out the timeout instead of being woken by the close";
    if (returned.load() != pollers)
        return std::to_string(returned.load()) + " of " + std::to_string(pollers) + " pollers returned";
    return {};
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
        std::fprintf(stderr, "usage: %s closerace [iterations] [pollers]\n", argv[0]);
        return 2;
    }
    const std::string scenario = argv[1];
    if (scenario == "closerace") {
        const int iterations = argc > 2 ? std::atoi(argv[2]) : 20;
        const int pollers = argc > 3 ? std::atoi(argv[3]) : 256;
        if (iterations <= 0 || pollers <= 0) {
            std::fprintf(stderr, "closerace needs a positive iteration and poller count\n");
            return 2;
        }
        return CloseRace(iterations, pollers);
    }
    std::fprintf(stderr, "unknown scenario '%s'\n", scenario.c_str());
    return 2;
}
