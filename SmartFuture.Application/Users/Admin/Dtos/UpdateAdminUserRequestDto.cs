namespace SmartFuture.Application.Users.Admin.Dtos;

// Phase 41 — narrow update DTO used by the admin Edit User drawer.
//
// Field permissions (enforced server-side in AdminUsersService.UpdateAsync):
//   * Any Admin: FirstName, LastName, AccountStatus on Customer/staff
//     rows that are NOT Super Admin.
//   * Super Admin only: Email, PhoneNumber.
//   * Role / UserType changes: NOT supported in this endpoint. A
//     dedicated flow will land in a later phase once last-role
//     invariants are designed.
//
// Each property is nullable — only set fields are applied. An empty
// string is treated as "no change" for name fields.
public class UpdateAdminUserRequestDto
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? AccountStatus { get; set; }
}
