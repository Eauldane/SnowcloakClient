using Snowcloak.Core.Analysis;

namespace Snowcloak.Services;

internal interface IAnalysisSource
{
    string DisplayName { get; }
    int CurrentFile { get; }
    int TotalFiles { get; }
    bool IsAnalysisRunning { get; }
    void CancelAnalyze();
    Task ComputeAnalysis(bool print = true, bool recalculate = false);
    AnalysisSnapshot GetLastAnalysisSnapshot();
}
