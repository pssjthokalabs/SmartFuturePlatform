namespace SmartFuture.Application.Users.Admin.Dtos;

// Phase 56 — payload for PUT /api/users/admin/{id}/role.
//
// SmartFuture keeps a 1-to-1 role relationship per user (the admin
// portal only ever assigns ONE primary type at a time). Changing role
// strips every Identity role from the user and reassigns just the
// requested one, so the post-change user is guaranteed to hold only
// the new role.
//
// Role values accepted (case-insensitive):
//   • "Customer"
//   • "Admin"
//   • "Technician"
//
// SuperAdmin / Agent / Support are NOT accepted via this endpoint:
//   - SuperAdmin promotion lives behind a separate, deliberately-
//     manual flow (see SeedSuperAdmin / DB script) so the portal can
//     never elevate a regular user to the highest privilege.
//   - Agent and Support roles aren't shippable yet (no per-role
//     workflows or rollups), mirroring CreateAsync's block.
public class ChangeAdminUserRoleRequestDto
{
    public string? Role { get; set; }
}
