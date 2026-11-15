# Spec Delta

## Purpose

Defines opt-in Jev assistance, which answers "what is this item?" for untyped purchases. It covers what is sent and when strong answers are written automatically, how weaker answers are reviewed, the deployment-wide limits, and how Jev's answers appear in the Uncategorized items view.

## ADDED Requirements

### Requirement: Opt-in consent
Assistance SHALL be off for the whole deployment until an Admin gives consent. Configured credentials, a passing connection check or `Jev:Enabled` SHALL NOT count as consent, alone or together. Only an Admin SHALL change assistance settings; anyone else SHALL receive 403.

#### Scenario: Configured but not consented
- **WHEN** Jev credentials are configured and verified but no Admin has consented
- **THEN** no request is sent to Jev

### Requirement: Capability-gated controls
While the server reports the assistance capability as missing, disabled, loading or unknown, every Jev control SHALL be absent from the client. Manual classification and receipt saving SHALL work unchanged, and the server SHALL make no outbound call to Jev.

#### Scenario: Capability unknown
- **WHEN** the client has not yet loaded the assistance capability
- **THEN** no Jev suggestion, Use button or historical-request control is shown

### Requirement: What is sent
A request to Jev SHALL contain only the receipt text, the store, and the taxonomy's names and definitions. It SHALL NOT contain price, date, quantity, account, any identifier, or envelope.

#### Scenario: Request payload
- **WHEN** an item is sent to Jev
- **THEN** the request carries its description, store and the taxonomy, and nothing about money, dates, accounts or envelopes

### Requirement: Only untyped items are asked about
Jev SHALL ask about and write to only items eligible for automation (see `purchase-classification`). It SHALL skip items the saving person set aside as **Later**, marked **Leave as is**, or excluded.

#### Scenario: Manually typed item
- **WHEN** a receipt is saved with one item the person classified and one untyped item
- **THEN** only the untyped item is sent to Jev

### Requirement: Strong matches are written automatically
With **Apply strong matches automatically** on (the default once consent exists), a fit of at least 0.7 with a root score of at least 0.90 SHALL write that root category. It SHALL use source `jev` and record the answer as JSON evidence. A child SHALL be written only when leaf writes are enabled (off by default) and the child clears its own threshold under the same root.

#### Scenario: Strong root match
- **WHEN** Jev returns fit 0.85 and root "Grocery" at 0.94, with leaf writes off
- **THEN** the item gets a `jev` assignment to "Grocery", and an activity entry records it

#### Scenario: Automatic writes switched off
- **WHEN** an Admin turns off Apply strong matches automatically and Jev returns the same strong match
- **THEN** nothing is written, and the answer is offered as a suggestion

### Requirement: Weaker answers, abstentions and failures
Any answer short of a strong match SHALL become a suggestion that needs a person's acceptance. A fit below 0.7 SHALL be an abstention: `no-fit` at or below 0.3, otherwise `uncertain-fit`. A provider failure or a malformed response SHALL write nothing, and SHALL NOT be recorded as an abstention.

#### Scenario: Moderate match
- **WHEN** Jev returns fit 0.8 and root "Household" at 0.75
- **THEN** no assignment is written, and the item is offered as a suggestion

#### Scenario: Provider error
- **WHEN** the Jev request times out
- **THEN** nothing is written, and the attempt is a failure, not an abstention

### Requirement: Accepting a suggestion
Accepting or correcting a suggestion SHALL write a `manual` assignment. It SHALL NOT create a mapping. Remembering a mapping SHALL always be a separate action.

#### Scenario: Accept
- **WHEN** a person accepts Jev's suggestion "Grocery > Dairy"
- **THEN** a `manual` assignment is written, and no mapping exists for the source afterwards

### Requirement: Deployment-wide limits
The daily limit SHALL be between 1 and 5,000 requests per UTC day, defaulting to 100 on a new installation, across the whole deployment. Once the pending limit is reached, new sources SHALL wait. A save of a source that already has a current attempt SHALL still attach to that attempt.

#### Scenario: Daily limit reached
- **WHEN** the deployment has sent its daily limit of requests today
- **THEN** no further request is sent until the next UTC day

### Requirement: Settings revisions retire outstanding work
A settings save that changes consent, provider, model or the automatic-write switch SHALL start a new settings revision. Attempts from the previous revision that are queued, suggested or abstained SHALL be marked `stale`; they leave review and stop counting against the pending limit. A save that changes only limits SHALL keep the revision.

#### Scenario: Model change
- **WHEN** an Admin changes the model while 5 suggestions await review
- **THEN** those 5 become `stale` and leave the review queue

#### Scenario: Limits-only change
- **WHEN** an Admin changes only the daily limit
- **THEN** outstanding suggestions remain decidable

### Requirement: Review preferences are private
Review preferences SHALL be private to the person who sets them, and SHALL never change a classification. They are `active`, `later`, `left_as_is`, and exclusions of up to 1000 items. Accepting a suggestion for an excluded item SHALL return 409 with a `detail`. So SHALL saving preferences against a stale preference revision.

#### Scenario: Accept an excluded item
- **WHEN** a person accepts a suggestion for an item they excluded
- **THEN** the server returns 409 with a `detail`

### Requirement: Bounded historical requests
An Admin SHALL be able to ask about past purchases explicitly, in a request bounded to 25 groups with at most 100 purchases each. A non-Admin SHALL receive 403. Assistance SHALL never scan or submit all history unasked.

#### Scenario: Too many groups
- **WHEN** an Admin requests 26 groups
- **THEN** the server returns 400 with a `detail`

### Requirement: Jev answers in the Uncategorized items view
When the assistance capability is available, each row in **Reports → Uncategorized items** SHALL show Jev's latest stored answer, if there is one. That is either the chosen type with its confidence and a one-tap **Use**, or "Jev wasn't sure". The row's picker SHALL list Jev's ranked alternatives first. A filter SHALL limit the list to answered or unanswered items.

#### Scenario: Use an answer
- **WHEN** a row shows "Grocery > Dairy (0.82)" and the person taps Use
- **THEN** a `manual` assignment to "Grocery > Dairy" is written, and the row leaves the list

#### Scenario: Abstained item
- **WHEN** Jev abstained on an item
- **THEN** its row shows "Jev wasn't sure"
