namespace Resort.Application.Common;

/// <summary>
/// The organisation a call with no signed-in user acts for (Sprint 11): Razorpay's webhook and the guest's pay page are
/// anonymous, so once they have matched a payment link (looked up across tenants on its unguessable id or token) they act
/// for that link's organisation. The tenant query filters and <see cref="ICurrentUser.OrganizationId"/> then work as for
/// staff; audit rows show no user ("System"). A signed-in caller's own organisation always wins.
/// </summary>
public sealed class SystemActor
{
    public Guid? OrganizationId { get; private set; }

    public void ActFor(Guid organizationId)
    {
        if (OrganizationId is { } current && current != organizationId)
            throw new InvalidOperationException("A request can act for one organisation only.");
        OrganizationId = organizationId;
    }
}
