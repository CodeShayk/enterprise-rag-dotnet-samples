using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Correction;

/// <summary>Thrown instead of calling a model the circuit breaker has given up on for now.</summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Stops calling a model that keeps failing. After <paramref name="failures"/> errors or timeouts in a row the circuit
/// opens for <paramref name="cooldown"/>, and calls fail at once instead of waiting on a model that isn't answering; the
/// loop then degrades to the evidence without an answer. The first call after the cooldown tries the model again.
/// </summary>
public sealed class ModelCircuitBreaker(IChatClient inner, int failures = 3, TimeSpan? cooldown = null, TimeSpan? timeout = null, Func<DateTimeOffset>? now = null)
    : DelegatingChatClient(inner)
{
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(120);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private int _failed;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public bool IsOpen => _now() < _openUntil;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfOpen();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var response = await base.GetResponseAsync(messages, options, deadline.Token);
            _failed = 0;
            return response;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failed(e);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfOpen();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var updates = base.GetStreamingResponseAsync(messages, options, deadline.Token).GetAsyncEnumerator(deadline.Token);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await updates.MoveNextAsync()) break;
                    update = updates.Current;
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Failed(e);
                }

                yield return update;
            }

            _failed = 0;
        }
        finally
        {
            await updates.DisposeAsync();
        }
    }

    private void ThrowIfOpen()
    {
        if (IsOpen) throw new ModelUnavailableException($"The model failed {failures} times in a row; trying again after {_openUntil:HH:mm:ss}.");
    }

    private ModelUnavailableException Failed(Exception e)
    {
        if (++_failed >= failures) _openUntil = _now() + _cooldown;
        return new ModelUnavailableException($"The model call failed: {e.Message}", e);
    }
}
