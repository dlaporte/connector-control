using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/FakeCommandLineTool.swift
///
/// The command-line tool's seam: on PATH or not as the test says, its link where the Mac's would be
/// (null, as on Windows, unless a test says otherwise), and Install and Remove held until <see cref="Finish"/>,
/// busy meanwhile, as an administrator prompt or a PATH broadcast holds them. Each start and end is
/// announced on <see cref="Changed"/>, and the last failure kept until the next try, as the real seams do.
/// </summary>
public sealed class FakeCommandLineTool : ICommandLineTool
{
    private Action<string?>? pending;

    public event Action? Changed;

    public string ToolPath { get; set; } = @"C:\Users\me\AppData\Local\Connector Control\bin\connector-control.exe";
    public string? InstalledLocation { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsBusy { get; private set; }
    public string? LastProblem { get; private set; }
    public int Installs { get; private set; }
    public int Removals { get; private set; }

    public void Install(Action<string?> completion)
    {
        Installs++;
        Start(completion);
    }

    public void Remove(Action<string?> completion)
    {
        Removals++;
        Start(completion);
    }

    private void Start(Action<string?> completion)
    {
        IsBusy = true;
        LastProblem = null;
        pending = completion;
        Changed?.Invoke();
    }

    /// <summary>Ends the operation in flight: the tool is then on PATH or not, and the operation answers
    /// <paramref name="problem"/>, null for done or cancelled.</summary>
    public void Finish(bool installed, string? problem = null)
    {
        IsInstalled = installed;
        IsBusy = false;
        LastProblem = problem;
        Changed?.Invoke();
        var done = pending;
        pending = null;
        done?.Invoke(problem);
    }
}
