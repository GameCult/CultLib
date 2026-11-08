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
    /// A write nothing answered within its window: neither admitted nor refused (write-receipt.md, "unconfirmed").
    /// </summary>
    public sealed class CultNetWriteUnconfirmedException : Exception
    {
        /// <summary>Creates the exception for the write <paramref name="messageId"/>.</summary>
        public CultNetWriteUnconfirmedException(string messageId)
            : base($"No admission or refusal arrived for write '{messageId}'.")
        {
            MessageId = messageId;
        }

        /// <summary>Gets the id of the unconfirmed write.</summary>
        public string MessageId { get; }
    }

    /// <summary>
    /// Completes a write on the peer's result frame, never on a send's return or a transport acknowledgement:
    /// an ack proves delivery, not admission.
    /// </summary>
    public static class CultNetWriteCompletion
    {
        /// <summary>How long after a send a refusal may still arrive when the caller names no window.</summary>
        public static readonly TimeSpan DefaultResultWindow = TimeSpan.FromMilliseconds(500);

        /// <summary>How long a receipt-capable peer has to answer a write (write-receipt.md, <c>RECEIPT_RESULT_WINDOW_MS</c>).</summary>
        public static readonly TimeSpan ReceiptResultWindow = TimeSpan.FromMilliseconds(5000);

        /// <summary>
        /// Sends one write to a receipt-capable peer and waits <paramref name="resultWindow"/> for its answer: a
        /// <c>write_admitted</c> naming <paramref name="messageId"/> returns, an <c>error.v1</c> naming it throws
        /// <see cref="CultNetWriteRefusedException"/>, and an id-less error (v0, or v1 with no id) is attributed to this write
        /// because it is the only one in flight on the connection. Silence throws <see cref="CultNetWriteUnconfirmedException"/>.
        /// </summary>
        public static async Task SendAndAwaitReceiptAsync(
            Func<Action<CultNetWriteAdmittedMessage>, Action<CultNetErrorMessage>, Action<CultNetErrorV1Message>, IDisposable?> subscribe,
            Action send,
            string messageId,
            TimeSpan resultWindow,
            CancellationToken cancellationToken = default)
        {
            if (subscribe == null) throw new ArgumentNullException(nameof(subscribe));
            if (send == null) throw new ArgumentNullException(nameof(send));
            var answer = new TaskCompletionSource<CultNetErrorMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = subscribe(
                admitted =>
                {
                    if (string.Equals(admitted.MessageId, messageId, StringComparison.Ordinal)) answer.TrySetResult(null);
                },
                error => answer.TrySetResult(error),
                error =>
                {
                    if (error.MessageId == null || string.Equals(error.MessageId, messageId, StringComparison.Ordinal))
                        answer.TrySetResult(error.ToV0());
                });
            send();
            var window = Task.Delay(resultWindow, cancellationToken);
            await Task.WhenAny(answer.Task, window).ConfigureAwait(false);
            if (!answer.Task.IsCompleted)
            {
                await window.ConfigureAwait(false);
                throw new CultNetWriteUnconfirmedException(messageId);
            }

            var refusal = await answer.Task.ConfigureAwait(false);
            if (refusal != null) throw new CultNetWriteRefusedException(refusal);
        }

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
