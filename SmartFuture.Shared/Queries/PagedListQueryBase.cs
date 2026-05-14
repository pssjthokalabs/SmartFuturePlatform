namespace SmartFuture.Shared.Queries;

public abstract class PagedListQueryBase
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private int _page = DefaultPage;
    private int _pageSize = DefaultPageSize;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? DefaultPage : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value < 1) _pageSize = DefaultPageSize;
            else if (value > MaxPageSize) _pageSize = MaxPageSize;
            else _pageSize = value;
        }
    }

    public string? Search { get; set; }
    public string? Status { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}
