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
- Fixing the "\_Amount" Bug when the appsettings show to the BigInteger "Amount" it does not map correctly when mapping (0)
  - temporarly fixing this bug mapping the string to a private field and then parsing it from the field

## Bitcoin

ExtPubKey has to be of a BIP84 Wallet.
