import type { ReactNode } from "react";
import { Link } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";

const PRIVACY_POLICY_LAST_UPDATED = "19 November 2026";

function Section({
  id,
  title,
  children,
}: {
  id: string;
  title: string;
  children: ReactNode;
}) {
  return (
    <section aria-labelledby={id} className="space-y-3">
      <h2 id={id} className="text-lg font-semibold">
        {title}
      </h2>
      {children}
    </section>
  );
}

/**
 * Public privacy policy (`/privacy`). Static text: it describes what the
 * software does, and names the deployment's operator as the party responsible
 * for the data. Every statement here was checked against the code; change the
 * text and the last-updated date together when behaviour changes.
 */
function PrivacyPolicy() {
  usePageTitle("Privacy policy");

  return (
    <article className="w-full max-w-3xl space-y-8 self-start text-sm leading-relaxed">
      <header className="space-y-2">
        <h1 className="page-title">Privacy policy</h1>
        <p className="text-muted-foreground">
          Last updated: {PRIVACY_POLICY_LAST_UPDATED}
        </p>
      </header>

      <Section id="privacy-responsible" title="Who is responsible">
        <p>
          Receipts is self-hosted software. The person or organisation running
          this deployment (its operator) is responsible for the data in it. The
          authors of the software do not receive this deployment's data.
        </p>
        <p>Questions and requests about your data go to the operator.</p>
      </Section>

      <Section
        id="privacy-ynab"
        title="What is obtained from YNAB and why"
      >
        <p>
          Nothing is obtained from YNAB unless an administrator connects YNAB
          to this deployment. Once connected, the app reads:
        </p>
        <ul className="list-disc space-y-1 pl-5">
          <li>Budget names and ids, to choose the budget.</li>
          <li>
            Account ids, names, types and balances, to map the app's accounts
            to YNAB accounts.
          </li>
          <li>
            Category ids, names and group names, to map envelopes and
            categories.
          </li>
          <li>
            Transactions on the dates being matched (date, amount, memo, payee,
            account and category), to match receipts to YNAB transactions and
            compare splits.
          </li>
        </ul>
        <p>The app also writes to YNAB:</p>
        <ul className="list-disc space-y-1 pl-5">
          <li>
            New transactions for receipts: the date, the amount, a payee taken
            from the receipt's store, a memo naming the receipt, and a category
            or split categories.
          </li>
          <li>Memo updates that add a link to the receipt.</li>
        </ul>
      </Section>

      <Section id="privacy-storage" title="How it is stored and secured">
        <p>The following is stored in this deployment's own database:</p>
        <ul className="list-disc space-y-1 pl-5">
          <li>The id of the selected budget.</li>
          <li>
            Account, category and envelope mappings, including YNAB account and
            category names and ids.
          </li>
          <li>
            For each pushed transaction, a sync record with the YNAB
            transaction id and the request that was sent.
          </li>
          <li>A log of sync attempts.</li>
          <li>
            Changes to envelope mappings, which appear in the audit log with
            the acting user and their IP address.
          </li>
        </ul>
        <p>
          Budgets, accounts and categories are also held in memory for a few
          minutes. Transactions fetched for matching are used and discarded;
          only the id of the matched transaction is kept.
        </p>
        <p>
          The OAuth tokens that give access to YNAB are encrypted before they
          are stored, are never shown in the app, and are not included in
          backups.
        </p>
        <p>
          Access to the app requires signing in. Connecting or disconnecting
          YNAB requires an administrator.
        </p>
      </Section>

      <Section id="privacy-retention" title="How long it is kept">
        <p>Data is kept until it is deleted.</p>
        <ul className="list-disc space-y-1 pl-5">
          <li>
            Mappings, sync records and the sync log stay until an administrator
            removes them or the data is deleted.
          </li>
          <li>Sign-in audit entries are removed after 180 days.</li>
          <li>
            Unaccepted AI name suggestions are removed after 30 days by
            default.
          </li>
          <li>
            Deleted records go to a recycle bin and are permanently removed
            when an administrator empties it, except that receipts with
            classification history are retained.
          </li>
        </ul>
      </Section>

      <Section id="privacy-third-parties" title="Third parties">
        <p>Data obtained from YNAB is not sent to any third party.</p>
        <p>
          Separately, other data is exchanged with the services below. Items
          (a) to (d) apply only if the operator has enabled them.
        </p>
        <ol className="list-[lower-alpha] space-y-1 pl-5">
          <li>
            Jev (typesafe.ai) category suggestions receive an item's receipt
            text, the store name, and the app's own category names and
            definitions. No prices, dates, accounts or YNAB data are sent.
          </li>
          <li>
            ChatGPT (OpenAI) name suggestions receive item descriptions,
            candidate product names and earlier receipt descriptions. No store
            names, prices, dates, accounts or YNAB data are sent.
          </li>
          <li>
            Error and performance monitoring (Sentry), when the operator's
            build has it configured, receives error reports and performance
            traces, which for signed-in users include the user id and email
            address, and browser session replays in which all text is masked.
          </li>
          <li>
            Telemetry is exported to a collector only if the operator
            configures one.
          </li>
          <li>
            The browser loads fonts from Google Fonts, which gives Google the
            visitor's IP address and browser details.
          </li>
          <li>
            The app downloads its language model files from Hugging Face
            without sending any user data.
          </li>
        </ol>
        <p>
          YNAB itself receives the data described above when it is connected.
        </p>
      </Section>

      <Section id="privacy-deletion" title="Deleting your data">
        <ul className="list-disc space-y-1 pl-5">
          <li>
            An administrator can disconnect YNAB in settings. This asks YNAB to
            revoke the app's access and removes the stored tokens.
          </li>
          <li>
            Access can also be removed in YNAB's account settings at any time.
          </li>
          <li>
            Records can be deleted in the app and permanently removed by an
            administrator.
          </li>
          <li>To have everything removed, ask the deployment's operator.</li>
        </ul>
      </Section>

      <p>
        <Link to="/" className="text-primary hover:underline">
          Back to the app
        </Link>
      </p>
    </article>
  );
}

export default PrivacyPolicy;
