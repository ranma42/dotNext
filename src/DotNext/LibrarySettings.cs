namespace DotNext;

internal static class LibrarySettings
{
    private const string StackallocThresholdEnvar = "DOTNEXT_STACK_ALLOC_THRESHOLD";
    private const int DefaultStackallocThreshold = 511;

    internal static int StackallocThreshold
    {
        get
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable(StackallocThresholdEnvar), out int result) || result < 16)
                result = DefaultStackallocThreshold;
            else if ((result & 1) == 0)
                result = checked(result - 1);

            return result;
        }
    }
}