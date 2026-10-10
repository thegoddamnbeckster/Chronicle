namespace Chronicle.Services.Notifications
{
    public enum TaskFailureKind
    {
        /// <summary>Something an administrator can act on: a network or service problem, bad or expired credentials,
        /// a full disk, a missing setting, a file that cannot be read.</summary>
        Fixable,
        /// <summary>A plugin built for a different version of Chronicle ("method not found", a type that will not load).
        /// Fixable only by updating that plugin, so it is announced only when an update exists.</summary>
        PluginIncompatible,
        /// <summary>A bug (null reference, bad cast, index out of range...). Nothing an administrator can do from the
        /// interface; it is logged and visible on the Background Tasks page, but not put in the bell.</summary>
        Internal,
    }

    /// <summary>Sorts a failed task's exception into what the person looking at the bell could do about it, so the bell
    /// only carries things that are theirs to fix.</summary>
    public static class TaskFailureTriage
    {
        public static TaskFailureKind Classify(Exception? exception)
        {
            var chain = Flatten(exception).ToList();
            if (chain.Count == 0) return TaskFailureKind.Fixable;
            // A version mismatch anywhere in the chain is the story; otherwise the root cause decides, so a bug wrapped
            // in a generic exception still reads as a bug and a network error wrapped in one as a network error.
            if (chain.Any(e => KindOf(e) == TaskFailureKind.PluginIncompatible)) return TaskFailureKind.PluginIncompatible;
            return KindOf(chain[^1]);
        }

        private static IEnumerable<Exception> Flatten(Exception? ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                yield return e;
                if (e is AggregateException agg)
                    foreach (var inner in agg.InnerExceptions.SelectMany(i => Flatten(i)))
                        yield return inner;
            }
        }

        private static TaskFailureKind KindOf(Exception e) => e switch
        {
            MissingMethodException or MissingMemberException or TypeLoadException or EntryPointNotFoundException
                or BadImageFormatException or FileLoadException or System.Reflection.ReflectionTypeLoadException
                => TaskFailureKind.PluginIncompatible,
            NullReferenceException or InvalidCastException or IndexOutOfRangeException or KeyNotFoundException
                or ArgumentNullException or ArgumentOutOfRangeException or NotImplementedException or InvalidProgramException
                or ArithmeticException or ArrayTypeMismatchException
                => TaskFailureKind.Internal,
            _ => TaskFailureKind.Fixable,
        };
    }
}
