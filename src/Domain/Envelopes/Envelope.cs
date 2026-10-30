namespace Domain.Envelopes;

/// <summary>Where money comes from. Archived (inactive) envelopes remain for history.</summary>
public class Envelope
{
	public Guid Id { get; set; }
	public string Name { get; set; }
	public bool IsActive { get; set; }

	public const string NameCannotBeEmpty = "Name cannot be empty";

	public Envelope(Guid id, string name, bool isActive = true)
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
