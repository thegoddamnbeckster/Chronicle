// The hosts these tests start share process-wide state (the media-type snapshot, the plugin policies), so they run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
