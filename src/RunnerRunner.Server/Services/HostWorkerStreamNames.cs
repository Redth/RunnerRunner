namespace RunnerRunner.Server.Services;

public static class HostWorkerStreamNames
{
    public const string StreamProviderName = "RunnerEvents";
    public const string ReconciliationStreamNamespace = "HostReconciliation";
    public const string ImageListStreamNamespace = "HostImageLists";
    public const string ImageRefreshStatusStreamNamespace = "HostImageRefreshStatus";
    public const string ImagePullProgressStreamNamespace = "HostImagePullProgress";
    public const string ImagePullCompleteStreamNamespace = "HostImagePullComplete";
    public const string ImageDeletedStreamNamespace = "HostImageDeleted";
    public const string HostLogsStreamNamespace = "HostLogs";
    public const string RunnerLogsStreamNamespace = "RunnerLogs";
}
