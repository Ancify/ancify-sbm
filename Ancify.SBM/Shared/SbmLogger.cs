using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Shared;

public class SbmLog { }

public static class SbmLogger
{
    private static ILogger? s_logger;

    public static void SetLoggerFromFactory(ILoggerFactory loggerFactory)
    {
        s_logger = loggerFactory.CreateLogger<SbmLog>();
    }

    public static ILogger? Get()
    {
        return s_logger;
    }
}