using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The confirmation dialog as a seam: it records every question, and answers each at once with
/// <see cref="Scripted"/>, or holds it until the test calls <see cref="Answer"/>. It can also throw,
/// to show a dialog that fails cannot wedge the write queue; a Swift confirmer cannot throw, so the
/// Mac's has no such switch.
///
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/FakeControlConfirmer.swift
/// </summary>
public sealed class FakeControlConfirmer : IControlConfirmer
{
    private readonly List<Action<bool>> waiting = [];

    /// <summary>Answers every question at once when set; null holds them for <see cref="Answer"/>.</summary>
    public bool? Scripted { get; set; }

    /// <summary>Thrown by every question, once recorded, when set.</summary>
    public Exception? Throws { get; set; }

    public List<ConfirmationRequest> Requests { get; } = [];

    public int WaitingCount => waiting.Count;

    public void Confirm(ConfirmationRequest request, Action<bool> completion)
    {
        Requests.Add(request);
        if (Throws is not null)
        {
            throw Throws;
        }
        if (Scripted is { } scripted)
        {
            completion(scripted);
        }
        else
        {
            waiting.Add(completion);
        }
    }

    /// <summary>The user answers the oldest question still open.</summary>
    public void Answer(bool allow)
    {
        var next = waiting[0];
        waiting.RemoveAt(0);
        next(allow);
    }
}
