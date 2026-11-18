namespace Allegiance.ClientCore.Tests;

/// <summary>
/// The training tests share the <em>process-global</em> <c>TrainingControlState</c> masks (a faithful
/// mirror of the native <c>Training.cpp</c> module globals), so they must not run in parallel with each
/// other — otherwise one test's <c>Reset</c>/<c>BeginMission</c> corrupts another's mid-flight assertions.
/// DisableParallelization also isolates this collection from other collections:
/// game-handler tests read the same global input masks and keymap. MonoGame
/// links this definition too; a Collection name without a definition does not
/// disable parallel execution against those readers (P1049b).
/// </summary>
[CollectionDefinition("Training", DisableParallelization = true)]
public class TrainingTestCollection
{
}
