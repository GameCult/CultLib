using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// A peer refused a write: the typed <c>cultnet.error.v0</c> it answered with.
    /// </summary>
    public sealed class CultNetWriteRefusedException : Exception
    {
        /// <summary>Creates the exception from the refusal frame.</summary>
        public CultNetWriteRefusedException(CultNetErrorMessage refusal)
            : base(refusal?.Error ?? throw new ArgumentNullException(nameof(refusal)))
        {
            Code = refusal.Code;
            Refusal = refusal;
        }

        /// <summary>Gets the machine-readable refusal code, e.g. <c>write_not_authorized</c> or <c>message_invalid</c>.</summary>
        public string? Code { get; }

        /// <summary>Gets the refusal frame as received.</summary>
        public CultNetErrorMessage Refusal { get; }
    }

    /// <summary>
    /// Completes a write on the peer's result frame, never on a send's return or a transport acknowledgement:
    /// an ack proves delivery, not admission.
    /// </summary>
    public static class CultNetWriteCompletion
    {
        /// <summary>How long after a send a refusal may still arrive when the caller names no window.</summary>
        public static readonly TimeSpan DefaultResultWindow = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Sends one write and waits <paramref name="resultWindow"/> for the peer's answer. A refusal frame rejects with
        /// <see cref="CultNetWriteRefusedException"/> whatever its order against the transport acknowledgement. A peer
        /// that admits a write sends no success frame, so the end of the window is the interim bound on a refusal
        /// (until peers send one); the error frame carries no message id, so any refusal inside the window is this
        /// write's.
        /// </summary>
        /// <param name="subscribe">Registers the refusal handler and returns its registration; called before the send.</param>
        /// <param name="send">Sends the write.</param>
        /// <param name="resultWindow">How long a refusal may take to arrive.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        public static async Task SendAndAwaitResultAsync(
            Func<Action<CultNetErrorMessage>, IDisposable?> subscribe,
            Action send,
            TimeSpan resultWindow,
            CancellationToken cancellationToken = default)
        {
            if (subscribe == null) throw new ArgumentNullException(nameof(subscribe));
            if (send == null) throw new ArgumentNullException(nameof(send));
            var refused = new TaskCompletionSource<CultNetErrorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = subscribe(error => refused.TrySetResult(error));
            send();
            var window = Task.Delay(resultWindow, cancellationToken);
            await Task.WhenAny(refused.Task, window).ConfigureAwait(false);
            if (refused.Task.IsCompleted) throw new CultNetWriteRefusedException(await refused.Task.ConfigureAwait(false));
            await window.ConfigureAwait(false);
        }
    }
}
