namespace Domain.Envelopes;

/// <summary>An optional purchase tag (an event, a holiday, a gift). Never required at entry.</summary>
public class Occasion
{
	public Guid Id { get; set; }
	public string Name { get; set; }
	public bool IsActive { get; set; }

	public const string NameCannotBeEmpty = "Name cannot be empty";

	public Occasion(Guid id, string name, bool isActive = true)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException(NameCannotBeEmpty, nameof(name));
		}

		Id = id;
		Name = name;
		IsActive = isActive;
	}
}
