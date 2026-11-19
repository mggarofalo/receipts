# Spec Delta

## Purpose

Defines how the deployment is authorized to act on a YNAB account: how an Admin connects and disconnects it, which credential is used for YNAB requests, how that credential is kept valid and protected, and what the app reports about the connection.

## ADDED Requirements

### Requirement: Admin starts a YNAB authorization
The system SHALL let an Admin start an OAuth authorization with YNAB and SHALL return the YNAB address the browser must visit. Each start SHALL create a single-use authorization attempt that expires after ten minutes and is bound to the Admin who started it. The grant requested SHALL allow writing to YNAB.

#### Scenario: Admin starts an authorization
- **WHEN** an Admin requests a new YNAB authorization on a deployment with OAuth client settings
- **THEN** the response is 200 with a YNAB authorization address carrying the configured client id, the configured redirect address, a state value unique to this attempt and an S256 code challenge

#### Scenario: OAuth client settings are missing
- **WHEN** an Admin requests a new YNAB authorization and the client id, client secret or redirect address is not configured
- **THEN** the response is 409 with a problem document whose `detail` says YNAB OAuth is not configured on this deployment

#### Scenario: Non-admin cannot start an authorization
- **WHEN** a signed-in user without the Admin role requests a new YNAB authorization
- **THEN** the response is 403 and no authorization attempt is created

### Requirement: Admin completes a YNAB authorization
The system SHALL complete an authorization when the Admin who started it presents the code and state returned by YNAB, exchanging the code for tokens and storing them as the deployment's YNAB connection. A completed or definitively rejected attempt SHALL NOT be usable again.

#### Scenario: Authorization completes
- **WHEN** the Admin who started an attempt presents its state and a code YNAB accepts
- **THEN** the response is 200 with the connection status reporting an OAuth connection, and later YNAB requests use the new connection

#### Scenario: Unknown, expired or used state
- **WHEN** an Admin presents a state that was never issued, has expired, or was already completed
- **THEN** the response is 400 with a problem document whose `detail` says the authorization attempt is not valid, and the existing connection is unchanged

#### Scenario: State presented by a different Admin
- **WHEN** an Admin presents a state that another Admin started
- **THEN** the response is 400 with a problem document and the existing connection is unchanged

#### Scenario: YNAB rejects the code
- **WHEN** an Admin presents a valid state and YNAB rejects the code
- **THEN** the response is 400 with a problem document whose `detail` says YNAB rejected the authorization, the attempt cannot be used again, and the existing connection is unchanged

#### Scenario: YNAB cannot be reached during completion
- **WHEN** an Admin presents a valid state and the request to YNAB fails without an answer
- **THEN** the response is 503, the existing connection is unchanged, and the same attempt can be completed again before it expires

#### Scenario: Connecting replaces an earlier connection
- **WHEN** an authorization completes while an OAuth connection already exists
- **THEN** the new tokens replace the stored ones and data cached from the earlier connection is not served for the new one

### Requirement: One deployment-wide connection
The system SHALL hold at most one YNAB OAuth connection for the whole deployment, shared by every signed-in user, and SHALL NOT keep a separate connection per user.

#### Scenario: Another user uses the connection
- **WHEN** an Admin has connected YNAB and a different signed-in user opens a YNAB feature
- **THEN** that feature works using the same connection

### Requirement: Credential used for YNAB requests
The system SHALL choose the credential for YNAB requests in this order: an OAuth connection when one is stored; otherwise the `YNAB_PAT` personal access token when it is set; otherwise none. When the OAuth connection requires reconnecting, the system SHALL NOT fall back to the personal access token.

#### Scenario: OAuth connection and personal access token both present
- **WHEN** an OAuth connection is stored and `YNAB_PAT` is also set
- **THEN** YNAB requests are sent with the OAuth access token

#### Scenario: Only the personal access token is set
- **WHEN** no OAuth connection is stored and `YNAB_PAT` is set
- **THEN** every YNAB feature behaves as it did before this capability existed

#### Scenario: Nothing is configured
- **WHEN** no OAuth connection is stored and `YNAB_PAT` is not set
- **THEN** endpoints that need YNAB answer 503 as they do today and the status reports that YNAB is not configured

#### Scenario: Reconnect required with a personal access token set
- **WHEN** the OAuth connection requires reconnecting and `YNAB_PAT` is set
- **THEN** YNAB requests are not sent with the personal access token and the status reports that reconnecting is required

### Requirement: Access tokens are renewed without user action
The system SHALL renew an OAuth access token before it expires, without any user action, and SHALL save the renewed tokens durably before sending any YNAB request with them. Concurrent requests SHALL result in at most one renewal.

#### Scenario: Token close to expiry
- **WHEN** a YNAB request is made and the stored access token is expired or about to expire
- **THEN** the system renews the tokens, stores them, and sends the request with the new access token

#### Scenario: Simultaneous requests during renewal
- **WHEN** several YNAB requests arrive at once while the access token needs renewing
- **THEN** exactly one renewal is sent to YNAB and every request proceeds with the renewed token

#### Scenario: Failure between renewal and storage
- **WHEN** the system receives renewed tokens but stops before storing them
- **THEN** after restart the previously stored tokens are still accepted by YNAB and the connection keeps working

#### Scenario: Renewal cannot reach YNAB
- **WHEN** a renewal attempt fails without an answer from YNAB
- **THEN** the connection stays stored, the YNAB request fails as a temporary YNAB failure, and a later request tries the renewal again

### Requirement: A rejected connection requires reconnecting
The system SHALL treat YNAB's refusal of the stored refresh token as loss of the connection: it SHALL discard the stored tokens, report that reconnecting is required, and record the event as a security-relevant warning without recording any token.

#### Scenario: Refresh token refused
- **WHEN** YNAB answers a renewal with an invalid-grant refusal
- **THEN** the stored tokens are discarded, the status reports that reconnecting is required, and endpoints that need YNAB answer 503

#### Scenario: Access token refused mid-life
- **WHEN** YNAB answers 401 to a request sent with an unexpired OAuth access token
- **THEN** the system attempts one renewal and repeats the request once if the renewal succeeds, or moves to reconnect required if the renewal is refused

#### Scenario: Reconnecting clears the state
- **WHEN** an Admin completes a new authorization while reconnecting is required
- **THEN** the status reports an OAuth connection again

### Requirement: Stored tokens are protected
The system SHALL store OAuth tokens only in encrypted form, SHALL NOT include any token, authorization code or client secret in an API response or a log entry, and SHALL treat stored tokens it cannot decrypt as requiring reconnection.

#### Scenario: Status never exposes tokens
- **WHEN** any user reads the connection status
- **THEN** the response contains no access token, refresh token or client secret

#### Scenario: Stored tokens cannot be decrypted
- **WHEN** the stored tokens cannot be decrypted, for example after the database is moved to a host with different encryption keys
- **THEN** no YNAB request is sent with them, the status reports that reconnecting is required, and the application keeps running

### Requirement: Admin disconnects YNAB
The system SHALL let an Admin disconnect the OAuth connection. Disconnecting SHALL ask YNAB to revoke the grant and SHALL remove the stored tokens whether or not YNAB could be reached, reporting which of the two happened.

#### Scenario: Disconnect with YNAB reachable
- **WHEN** an Admin disconnects and YNAB accepts the revocation request
- **THEN** the response is 200 reporting that remote revocation succeeded, and the stored tokens are gone

#### Scenario: Disconnect with YNAB unreachable
- **WHEN** an Admin disconnects and the revocation request fails
- **THEN** the response is 200 reporting that remote revocation failed, the stored tokens are gone, and the user is told to remove the application's access in YNAB

#### Scenario: Disconnect with nothing connected
- **WHEN** an Admin disconnects and no OAuth connection is stored
- **THEN** the response is 200 reporting that remote revocation was not attempted

#### Scenario: Disconnect clears a reconnect-required state
- **WHEN** an Admin disconnects while reconnecting is required
- **THEN** the state is cleared and credential selection falls back to `YNAB_PAT` if it is set

#### Scenario: Non-admin cannot disconnect
- **WHEN** a signed-in user without the Admin role requests a disconnect
- **THEN** the response is 403 and the connection is unchanged

### Requirement: Connection status reports the credential in use
The connection status SHALL report which credential is in use (none, OAuth or personal access token), whether OAuth can be started on this deployment, whether reconnecting is required, and when the OAuth connection was made. Existing status properties SHALL keep their meaning, with "configured" true whenever a usable credential exists.

#### Scenario: Connected with OAuth
- **WHEN** a signed-in user reads the status with a working OAuth connection
- **THEN** it reports the credential as OAuth, configured and connected as true, and the time the connection was made

#### Scenario: Personal access token only
- **WHEN** a signed-in user reads the status with only `YNAB_PAT` set
- **THEN** it reports the credential as personal access token and the existing properties as they are today

#### Scenario: OAuth available but not connected
- **WHEN** a signed-in user reads the status on a deployment with OAuth client settings and no credential
- **THEN** it reports the credential as none, configured as false, and OAuth as available

#### Scenario: Reconnect required
- **WHEN** a signed-in user reads the status after the connection was rejected
- **THEN** it reports that reconnecting is required and connected as false

### Requirement: Settings page offers connect and disconnect
The YNAB settings page SHALL show the connection state and, to an Admin, the action that fits it: connect when OAuth is available and nothing is connected, reconnect when reconnecting is required, and disconnect when an OAuth connection exists. It SHALL explain how to enable the integration when neither OAuth nor a personal access token is configured.

#### Scenario: Admin connects from the page
- **WHEN** an Admin chooses Connect, approves the request at YNAB, and is returned to the app
- **THEN** the page shows the connection as connected with OAuth and the code and state are no longer in the browser address

#### Scenario: Admin declines at YNAB
- **WHEN** an Admin is returned to the app after declining the request at YNAB
- **THEN** the page says the connection was not made and the previous state is unchanged

#### Scenario: Non-admin sees state but no actions
- **WHEN** a signed-in user without the Admin role opens the page
- **THEN** the connection state is shown and no connect, reconnect or disconnect action is offered

#### Scenario: Disconnect asks for confirmation
- **WHEN** an Admin chooses Disconnect
- **THEN** the page asks for confirmation before disconnecting and afterwards shows the resulting state, including when remote revocation failed

### Requirement: Tokens stay out of the portable backup
The portable backup SHALL NOT contain OAuth tokens or any other part of the YNAB OAuth connection, and restoring a backup SHALL leave the deployment's current connection as it was.

#### Scenario: Export with a connection
- **WHEN** an Admin exports a backup while an OAuth connection exists
- **THEN** the backup contains no table or value derived from the connection

#### Scenario: Restore with a connection
- **WHEN** an Admin restores a backup onto a deployment with an OAuth connection
- **THEN** the connection is still in place afterwards
