using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.LinkTemplate;

// Records that DescriptionId is the item ItemTemplateId already describes (RECEIPTS-930).
//
// The result's Survivor is the template's entry, which is not necessarily DescriptionId: unless
// that row already was the template's entry, it is consolidated into it and deleted. Merged says
// which of the two happened.
public record LinkItemTemplateCommand(Guid DescriptionId, Guid ItemTemplateId) : ICommand<LinkTemplateResult>;
