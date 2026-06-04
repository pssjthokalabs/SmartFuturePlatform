namespace SmartFuture.Application.Users.Admin.Dtos;

// Real counts across the full user set, independent of the current
// filter / page. The page uses these to render the user-type chip row.
public class AdminUserTypeCountsDto
{
    public int All { get; set; }
    public int Customers { get; set; }
    public int Admins { get; set; }
    public int Agents { get; set; }
    public int Technicians { get; set; }
    public int Support { get; set; }

    // Controlled QA test accounts. Isolated from All/Customers; surfaced only
    // under its own chip so QA can find sandbox users without polluting real
    // counts.
    public int Test { get; set; }
}
