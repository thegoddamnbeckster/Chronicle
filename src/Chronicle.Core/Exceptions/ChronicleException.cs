namespace Chronicle.Core.Exceptions
{
    public class ChronicleException : Exception
    {
        public ChronicleException(string message) : base(message) { }
        public ChronicleException(string message, Exception inner) : base(message, inner) { }
    }

    public class MediaNotFoundException : ChronicleException
    {
        public MediaNotFoundException(int id) : base($"Media item {id} was not found.") { }
    }

    public class UserNotFoundException : ChronicleException
    {
        public UserNotFoundException(int id) : base($"User {id} was not found.") { }
        public UserNotFoundException(string username) : base($"User '{username}' was not found.") { }
    }

    public class DuplicateUsernameException : ChronicleException
    {
        public DuplicateUsernameException(string username) : base($"Username '{username}' is already taken.") { }
    }

    public class InvalidCredentialsException : ChronicleException
    {
        public InvalidCredentialsException() : base("Invalid username or password.") { }
    }

    public class LibraryEntryNotFoundException : ChronicleException
    {
        public LibraryEntryNotFoundException(int id) : base($"Library entry {id} was not found.") { }
    }

    public class MediaListNotFoundException : ChronicleException
    {
        public MediaListNotFoundException(int id) : base($"List {id} was not found.") { }
    }

    public class MediaListItemNotFoundException : ChronicleException
    {
        public MediaListItemNotFoundException(int id) : base($"List item {id} was not found.") { }
    }

    public class DuplicateListItemException : ChronicleException
    {
        public DuplicateListItemException(int mediaItemId)
            : base($"Media item {mediaItemId} is already in this list.") { }
    }

    public class DeviceAuthCodeNotFoundException : ChronicleException
    {
        public DeviceAuthCodeNotFoundException() : base("Device auth code not found or expired.") { }
    }

    public class DeviceAuthCodeExpiredException : ChronicleException
    {
        public DeviceAuthCodeExpiredException() : base("Device auth code has expired. Please start a new connection.") { }
    }

    public class DeviceAuthCodeAlreadyUsedException : ChronicleException
    {
        public DeviceAuthCodeAlreadyUsedException() : base("Device auth code has already been used.") { }
    }
}
