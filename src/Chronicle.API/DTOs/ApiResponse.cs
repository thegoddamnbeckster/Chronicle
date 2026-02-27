namespace Chronicle.API.DTOs
{
    public class ApiResponse<T>
    {
        public bool Success { get; init; }
        public T? Data { get; init; }
        public ApiError? Error { get; init; }
        public PaginationInfo? Pagination { get; init; }

        public static ApiResponse<T> Ok(T data, PaginationInfo? pagination = null) =>
            new() { Success = true, Data = data, Pagination = pagination };

        public static ApiResponse<T> Fail(string code, string message) =>
            new() { Success = false, Error = new ApiError(code, message) };
    }

    public record ApiError(string Code, string Message);

    public record PaginationInfo(int Page, int PerPage, int? Total);
}
