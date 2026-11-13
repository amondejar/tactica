namespace Shop.Kernel;

public interface IDomainEvent;

public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;
    protected Entity(TId id) => Id = id;
    protected Entity() { }
    public override bool Equals(object? obj) => obj is Entity<TId> other && other.GetType() == GetType() && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}

public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    private readonly List<IDomainEvent> _events = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _events;
    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() { }
    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);
    public void ClearDomainEvents() => _events.Clear();
}

public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();
    public override bool Equals(object? obj) => obj is ValueObject other && other.GetType() == GetType() && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    public override int GetHashCode() => GetEqualityComponents().Aggregate(0, HashCode.Combine);
}
