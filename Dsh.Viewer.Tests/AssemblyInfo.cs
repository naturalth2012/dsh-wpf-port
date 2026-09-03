using Xunit;

// Viewer tests assume a shared zh-CN default UI culture (e.g. SurfaceItem role labels) and one
// test temporarily switches the global Localization culture; disable xunit class-level parallelism
// so tests never observe the global culture mid-switch.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
