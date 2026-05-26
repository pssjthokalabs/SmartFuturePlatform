namespace SmartFuture.Shared.Enums.ServiceChanges;

// When the change actually takes effect.
//
//   - Immediate: upgrade-only path. Customer pays a pro-rata difference
//     today; on payment received the NetworkAccount swaps to the new
//     package right away.
//   - NextCycle: downgrade-only path. No charge today; the swap is
//     scheduled for the next billing-cycle anchor date and applied
//     either by an admin or by the future invoice generator.
public enum ServiceChangeEffectiveMode
{
    Immediate = 0,
    NextCycle = 1
}
