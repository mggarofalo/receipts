import { useMemo } from "react";
import { Link } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { PageHead } from "@/components/primitives";
import { NamedLibrarySection } from "@/components/envelopes/NamedLibrarySection";
import { ItemTypeDefaultsSection } from "@/components/envelopes/ItemTypeDefaultsSection";
import {
  useCreateEnvelope,
  useCreateOccasion,
  useDeleteEnvelope,
  useDeleteOccasion,
  useEnvelopes,
  useOccasions,
  useUpdateEnvelope,
  useUpdateOccasion,
} from "@/hooks/useEnvelopes";
import { useYnabEnvelopeMappings } from "@/hooks/useYnab";

export default function EnvelopeSettings() {
  usePageTitle("Envelopes");
  const envelopes = useEnvelopes();
  const createEnvelope = useCreateEnvelope();
  const updateEnvelope = useUpdateEnvelope();
  const deleteEnvelope = useDeleteEnvelope();
  const occasions = useOccasions();
  const createOccasion = useCreateOccasion();
  const updateOccasion = useUpdateOccasion();
  const deleteOccasion = useDeleteOccasion();
  const ynabLinks = useYnabEnvelopeMappings();
  // Envelopes can share a name across YNAB groups; the group tells them apart.
  const ynabLabels = useMemo(
    () =>
      new Map(
        ynabLinks.mappings.map((link) => [
          link.envelopeId,
          `${link.ynabCategoryGroupName}: ${link.ynabCategoryName}`,
        ]),
      ),
    [ynabLinks.mappings],
  );

  return (
    <div className="space-y-6">
      <PageHead
        title="Envelopes"
        sub="Where money comes from, separate from what an item is"
      />
      <p className="text-sm text-muted-foreground">
        Each purchase records its envelope when saved: a per-item override, else
        the receipt&rsquo;s &ldquo;paid from&rdquo; envelope, else its item
        type&rsquo;s default. Link envelopes to YNAB categories in{" "}
        <Link to="/settings/ynab" className="underline">
          YNAB settings
        </Link>
        .
      </p>
      <NamedLibrarySection
        title="Envelopes"
        description="Archive an envelope to stop choosing it; purchases that used it keep it."
        noun="envelope"
        items={envelopes.envelopes}
        isLoading={envelopes.isLoading}
        isError={envelopes.isError}
        busy={
          createEnvelope.isPending ||
          updateEnvelope.isPending ||
          deleteEnvelope.isPending
        }
        onCreate={(name) => createEnvelope.mutateAsync(name)}
        onUpdate={(item) => updateEnvelope.mutateAsync(item)}
        onDelete={(id) => deleteEnvelope.mutateAsync(id)}
        detail={
          ynabLinks.isSuccess
            ? (item) => {
                const label = ynabLabels.get(item.id);
                return label ? `YNAB: ${label}` : "Not linked to YNAB";
              }
            : undefined
        }
      />
      <ItemTypeDefaultsSection envelopes={envelopes.envelopes} />
      <NamedLibrarySection
        title="Occasions"
        description="Optional tags such as an event or a holiday. Never required at entry."
        noun="occasion"
        items={occasions.occasions}
        isLoading={occasions.isLoading}
        isError={occasions.isError}
        busy={
          createOccasion.isPending ||
          updateOccasion.isPending ||
          deleteOccasion.isPending
        }
        onCreate={(name) => createOccasion.mutateAsync(name)}
        onUpdate={(item) => updateOccasion.mutateAsync(item)}
        onDelete={(id) => deleteOccasion.mutateAsync(id)}
      />
    </div>
  );
}
