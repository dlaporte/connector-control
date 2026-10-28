using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The confirmation dialog as a seam: it records every question, and answers each at once with
/// <see cref="Scripted"/>, or holds it until the test calls <see cref="Answer"/>.
///
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/FakeControlConfirmer.swift
/// </summary>
public sealed class FakeControlConfirmer : IControlConfirmer
{
    private readonly List<Action<bool>> waiting = [];

    /// <summary>Answers every question at once when set; null holds them for <see cref="Answer"/>.</summary>
    public bool? Scripted { get; set; }

    public List<ConfirmationRequest> Requests { get; } = [];

    public int WaitingCount => waiting.Count;

    public void Confirm(ConfirmationRequest request, Action<bool> completion)
    {
        Requests.Add(request);
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
