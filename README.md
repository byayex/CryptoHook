# CryptoHook

A simple, self-hosted, open-source cryptocurrency payment processor.

## General

- Only one transaction per payment (reduces overhead and problems for us.) | maybe something for later
- Maybe adding functionality to route traffic over the tor network
- We use general sqlite at first ( if needed adding postgres later is not a problem)
- Its only possible to pay with ONE transaction and than paying the EXACT amount ANYTHING ELSE WILL mark the payment as NOT done
  - This also means that it is need to manually return the coins to the sender (because we never store or get privkeys it can not be automated)
- For simplicity i created everything with one api later we will do following:
  - use one electrum server to get the data
    - cross-validating this through two / three different apis
      - routing everything over tor network
- Its important that if someone starts using this he HAS to use a new hd wallet because we just start at 0
- We use BigInteger and this has to be respected on the system the receives the api (mostly frontend)
- Use WebHooks to push payment updates.
  - The WebHook will have the "X-Signature" header which will contains the secret for validation

## Bitcoin

ExtPubKey has to be of a BIP84 Wallet.
