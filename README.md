# CryptoHook

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)

---

## ✨ Project Status: Low Maintenance & Community Driven

**This project is currently in a low-maintenance mode, and community contributions are welcome!**

While I have limited time to dedicate to developing new features myself, I am happy to review and merge pull requests for bug fixes and new features.

Please be patient, as review times may vary, but I will do my best to get to all contributions. Your support and collaboration are greatly appreciated in helping CryptoHook grow!

---

## Overview

CryptoHook is an open-source, self-hosted Cryptocurrency Payment API. It was designed with a focus on privacy and giving users full control over their payment infrastructure.

The existing code is from an early development stage and should be considered a prototype. It is not recommended for production use.

## Original Vision & Potential Features

The following list outlines the features and improvements that were originally planned for CryptoHook. This can serve as inspiration or a starting point for anyone interested in contributing to the project.

- [ ] **Broader Cryptocurrency Support**: Extend beyond the initial set of currencies (only bitcoin currently).
- [x] **CI/CD Integration**: Implement a pipeline for automated testing and deployment.
- [ ] **Architectural Shift**: Transition from REST to RPC for improved performance (for checking for payments on the blockchain).
- [x] **Enhanced Security**: Implement more robust authentication and security hardening.
- [x] **Docker Support**: Provide a `Dockerfile` for easy, containerized deployment.
- [ ] **Expanded Documentation**: Comprehensive setup guides, API references, and examples.

## Getting Started

1. Clone the repository:

   ```sh
   git clone https://github.com/byayex/CryptoHook.git
   ```

2. Open the solution in your preferred .NET IDE (e.g., Visual Studio, JetBrains Rider). **|** I recommend using VS Code and opening the Workspace in the root directory
3. Review the `CryptoHook.Api` and unit test projects to understand the basic structure.

### Required configuration

The API refuses to start until it is configured, rather than starting in an insecure or
non-functional state:

- **`ApiKeys`** – at least one key of 16 characters or more. Authentication is deny by default:
  every routed endpoint requires a matching `X-API-Key` header unless it is explicitly marked
  `[AllowAnonymous]`, and requests without one are rejected with `401`. Supply keys through user
  secrets or environment variables (`ApiKeys__0`), not through a checked-in `appsettings.json`;
  the shipped files leave the section empty on purpose, so an unconfigured deployment fails to
  start. Keys are re-read when the configuration changes, so rotating one needs no restart.
- **`Webhooks[].Url` and `Webhooks[].Secret`** – every configured subscriber needs both; the secret
  must be at least 32 characters and is supplied the same way (`Webhooks__0__Secret`). Both are
  checked at startup, so a placeholder secret or a missing URL fails the deployment instead of
  surfacing later as unsigned-in-practice notifications or as payment updates that cannot be saved.
- **`ForwardedHeaders:KnownProxies` / `:KnownNetworks`** – required if anything sits in front of this
  service. Only a loopback proxy is trusted by default, so otherwise `X-Forwarded-For` is discarded:
  HTTPS enforcement sees a plaintext request and every caller behind the proxy is rate limited as one.
  Name the proxy rather than widening the trust, because whatever is trusted here can choose the
  client address the rate limiter partitions on.
- **`CurrencyConfigs[].ExtPubKey`** – the account level extended public key that receiving
  addresses are derived from. Ethereum networks also expect an `xpub` prefixed key, since the
  key format carries no meaning for Ethereum addresses.
- **`CurrencyConfigs[].ExplorerApiKey`** – optional, but Etherscan throttles and eventually
  rejects keyless requests.
- **`CurrencyConfigs[].MaxPendingRequests`** – how many payment requests may be outstanding
  per currency and network (default 100). Payment creation is additionally rate limited to 30
  requests per minute.
- **`CurrencyConfigs[].AddressGapLimit`** – how many consecutive derivation indices may go
  unused before payment creation is refused with `503` (default 20). Every request permanently
  consumes an index whether or not it is ever paid, and a watch-only wallet stops scanning after
  a run of addresses with no history. **This must not exceed the gap limit your receiving wallet
  actually scans with**, or the wallet will stop discovering payments made to addresses this
  service is still handing out.
- **`CurrencyConfigs[].Confirmations[].ConfirmationsNeeded`** – must be at least 1; startup fails
  otherwise. Zero would settle a payment straight out of the mempool, where the payer can still
  replace it.
- **`CurrencyConfigs[].AmountTolerance`** – how far a received total may deviate from the
  expected amount, in the currency's smallest unit, and still count as exact (default `"0"`,
  meaning an exact match). Without a tolerance a payment a single satoshi or wei short stays
  underpaid.
- **`CurrencyConfigs[].ExplorerRequestsPerSecond`** – paces requests to the block explorer
  (default 4; Etherscan's free tier allows 5).
- **`PaymentCheck`** – the polling loop. `CheckIntervalSeconds` × `MaxConcurrentChecks` has to be
  enough to get through the watched payments; each one costs two to three explorer requests per
  pass, and a single check is abandoned for the pass once it exceeds `CheckIntervalSeconds`.
  `MissingFundsGraceHours` is how long a payment keeps its status after the explorer stops
  reporting its transaction, before the disappearance is believed.
- **`WebhookDelivery`** – the retry policy for notifications.
- **`ConnectionStrings:Database`** – optional; defaults to `Data Source=CryptoHook.db`.
- **`HttpsRedirectionPort`** – required for HTTPS redirection to do anything. Without it (and
  without `ASPNETCORE_HTTPS_PORTS`) the redirection middleware is inactive, so API keys can be
  accepted over plaintext HTTP; the API logs a warning at startup when that is the case. Behind a
  TLS-terminating proxy, forwarded headers are honoured.

## How payments are settled

A payment request moves through `Pending` → `Paid`/`Overpaid` → `Confirmed`/`OverpaidConfirmed`,
or ends at `Expired`. What is worth knowing before relying on it:

- **All inbound transactions to the address are summed.** Paying in two transfers, or topping up
  an underpayment, resolves normally. `TransactionId` is then a comma-separated list, and the
  reported confirmation count is that of the *least* confirmed part.
- **Overpayments settle.** `OverpaidConfirmed` means the chain is done with it; the surplus is
  yours to refund or keep.
- **`Underpaid` is not terminal, but it is not watched forever.** A request that never received
  the full amount is abandoned to `Expired` once `PaymentCheck.AbandonAfterExpiryHours` has passed
  since expiry, with `AmountPaid` preserved so partial funds can be refunded out of band.
- **Late payments are accepted and flagged.** Funds arriving after a request was reported as
  `Expired` still settle, but `wasLate` is set on the payment for good. Treat those separately:
  the order may already have been cancelled, and the amount was quoted against a rate that is no
  longer current.
- **Settlement is watched for reorganisations** for `PaymentCheck.ReorgWatchHours` after the fact.
  A reorg deeper than your confirmation threshold is logged at critical level; the service cannot
  undo it, so choose `ConfirmationsNeeded` for the value at risk.
- **`MultipleTransactions` is no longer produced.** It survives in the enum so that rows written
  by older versions still deserialise; the worker re-evaluates them and moves them on.

Amounts are always given and returned in the currency's smallest unit (satoshi, wei) as JSON
strings. There is no fiat conversion or rate locking — quoting is the integrator's job, and
`InitialPaymentTimeout` is the only exchange-rate protection the service offers.

## Webhooks

Every status change is recorded as a `WebhookDelivery` row before anything is sent, then
delivered by a background dispatcher that retries with exponential backoff. Notifications are
**at-least-once**: a subscriber must deduplicate on the envelope's `eventId`.

The body is a versioned envelope:

```json
{
  "specVersion": "1",
  "eventId": "0d6e...",
  "eventType": "payment.confirmed",
  "occurredAt": "2026-01-01T12:00:00Z",
  "data": { "id": "...", "status": "Confirmed", "amountPaid": "100000", "wasLate": false }
}
```

Verify the `X-Signature` header as `sha256=` plus the lower-case hex HMAC-SHA256 of
`{X-Timestamp}.{X-Request-ID}.{body}` using your configured secret. The timestamp and
`X-Request-ID` are generated per attempt, so a retry carries fresh ones and stays inside a
freshness window; deduplicate on the envelope's `eventId` or on `X-Delivery-ID`, which are stable
across attempts. A confirmation count that merely ticked up does not produce a new notification,
so `payment.confirmed` arrives once per settlement rather than once per block.

### Breaking changes for subscribers

Both of these landed together and both require a change on the subscriber's side before upgrading:

- **The signature is now lower-case hex.** It was previously upper-case, so any subscriber that
  compares `X-Signature` byte for byte — which is what a fixed-time comparison does — will reject
  every notification until it is updated. Compare case-insensitively, or lower-case both sides.
- **The body is now the envelope shown above**, not the payment object on its own. What used to be
  the whole body is now the `data` member.

The retry budget is smaller than it looks: eight attempts from a fifteen second initial backoff
spans roughly **thirty minutes**, not hours. Raise `WebhookDelivery.MaxAttempts` if a subscriber
outage needs to be survived for longer than that.

If a subscriber stays down past `WebhookDelivery.MaxAttempts`, the delivery is kept as a dead
letter rather than dropped. Find and requeue those through
`GET /api/v1/webhook-deliveries?status=Failed` and `POST /api/v1/webhook-deliveries/{id}/replay`.
Polling `GET /api/v1/payments/{id}` remains the authoritative fallback.

### Operational limits

- The payment worker and the webhook dispatcher both assume a **single instance**. Deliveries are
  leased for the duration of a send rather than locked, so running two against one database
  would still send some notifications
  twice.
- Bitcoin support reads from Blockstream Esplora and Ethereum from Etherscan; both are external
  dependencies with their own availability. An explorer that cannot be read leaves payment
  statuses untouched rather than reporting them as unpaid.
- Ethereum watches external *and* internal transfers, so contract-originated payments (Safe and
  other smart-contract wallets, several exchange withdrawal systems) are detected. ERC-20 and
  other token transfers are not supported — only native ETH.

## Contributing

Contributions are the primary way this project will move forward! If you have a bug fix or a new feature, please feel free to:

1. **Fork the repository.**
2. Create your feature branch (`git checkout -b feature/AmazingFeature`).
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`).
4. Push to the branch (`git push origin feature/AmazingFeature`).
5. **Open a Pull Request.**

I will review all PRs and provide feedback as my time allows. Thank you for helping out!

## License

This project is licensed under the [GNU GPLv3 License](https://www.gnu.org/licenses/gpl-3.0).
