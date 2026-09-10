namespace Application.Models.Ynab;

public record YnabPushOperationIdentity(Guid LocalTransactionId, string ImportId);
