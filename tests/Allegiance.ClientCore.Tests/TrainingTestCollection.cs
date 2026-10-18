namespace Allegiance.ClientCore.Tests;

/// <summary>
/// The training tests share the <em>process-global</em> <c>TrainingControlState</c> masks (a faithful
/// mirror of the native <c>Training.cpp</c> module globals), so they must not run in parallel with each
/// other — otherwise one test's <c>Reset</c>/<c>BeginMission</c> corrupts another's mid-flight assertions.
/// xUnit still runs this collection in parallel with <em>other</em> collections, which is safe because no
/// other test touches the training mask state.
/// </summary>
[CollectionDefinition("Training", DisableParallelization = true)]
public class TrainingTestCollection
{
}
