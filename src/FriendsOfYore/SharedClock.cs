using System;

namespace FriendsOfYore
{
    /// <summary>
    /// Seconds on the computer clock (UTC; Windows keeps it in sync), the same on every PC. Mills and goats are a function
    /// of it, so they agree without messages and keep their place through pauses and alt-tab.
    /// </summary>
    public static class SharedClock
    {
        static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static double Now => (DateTime.UtcNow - Epoch).TotalSeconds;
    }
}
