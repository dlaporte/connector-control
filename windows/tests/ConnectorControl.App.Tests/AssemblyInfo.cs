// Every test body in this assembly runs on the one WPF dispatcher WpfApp owns, so running test
// classes in parallel buys no wall-clock time here. What it does buy is re-entrancy: a class whose
// body is waiting its turn on that dispatcher gets to run nested inside another's, at the
// DataBind pump WindowTestSupport.Layout opens — and a window the first class has on screen is
// then the owner WpfDialogs.ResolveOwner() hands the second. That is how DialogTests came to see
// an editor window as its owner. Run the classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
