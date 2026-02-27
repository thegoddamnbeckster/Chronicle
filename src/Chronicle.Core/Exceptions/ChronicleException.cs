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
}
