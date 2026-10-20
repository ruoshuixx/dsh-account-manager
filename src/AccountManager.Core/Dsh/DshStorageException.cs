namespace AccountManager.Core.Dsh;

/// <summary>
/// Error of the dsh backend and the services built on it. Messages are user-facing Chinese text and
/// never contain secret values.
/// </summary>
public class DshException : Exception
{
    public DshException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Base error of the dsh storage layer (files, locks, formats).</summary>
public class DshStorageException : DshException
{
    public DshStorageException(string message) : base(message) { }

    public DshStorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A stored document is not in the exact shape dsh accepts; nothing was written.</summary>
public sealed class DshFormatException : DshStorageException
{
    public DshFormatException(string message) : base(message) { }

    public DshFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The cross-process writer lock could not be acquired before the deadline.</summary>
public sealed class DshLockTimeoutException : DshStorageException
{
    public DshLockTimeoutException(string lockPath)
        : base("timed out waiting for the writer lock at " + lockPath) { LockPath = lockPath; }

    public string LockPath { get; }
}
