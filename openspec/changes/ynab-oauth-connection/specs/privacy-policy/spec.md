# Spec Delta

## Purpose

Defines the privacy policy the app publishes about the data it holds and the external services it exchanges data with, so that a person connecting an account such as YNAB can read what happens to their data first.

## ADDED Requirements

### Requirement: Privacy policy is publicly readable
The system SHALL publish a privacy policy at a stable address in the app that can be read without signing in.

#### Scenario: Reading without an account
- **WHEN** a visitor who is not signed in opens the privacy policy address
- **THEN** the full policy is shown without redirecting to sign-in

### Requirement: Privacy policy covers data obtained from YNAB
The privacy policy SHALL state what data the app obtains from YNAB and why, how that data is stored and secured and how long it is kept, that it is not passed to third parties without the user knowing, how a user has their data deleted, and the date the policy was last updated.

#### Scenario: Required statements are present
- **WHEN** the privacy policy is read
- **THEN** it contains each of: purposes of use, storage and security, retention, third-party sharing, how to request deletion, and a last-updated date

#### Scenario: Third-party statement matches behaviour
- **WHEN** the app sends data to an external classification or normalization provider
- **THEN** the policy names that provider and says which kinds of data are sent, including whether any YNAB-sourced data is among them

### Requirement: Privacy policy is linked where YNAB is connected
The YNAB settings page SHALL link to the privacy policy next to the connection controls.

#### Scenario: Link beside the connect action
- **WHEN** an Admin opens the YNAB settings page
- **THEN** a link to the privacy policy is shown with the connection controls
