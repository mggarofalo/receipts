namespace Application.Models.Reports;

public record ItemCostBucket(
	string Period,
		decimal Amount,
		string Currency = "");

public record ItemCostOverTimeResult(
	List<ItemCostBucket> Buckets);
