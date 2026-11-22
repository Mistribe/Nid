# RFC 9421 test vectors

- `b24-response.txt`: RFC 9421 Appendix B.2 `test-response` message with the `Signature-Input` and `Signature` fields of
  §B.2.4 (`ecdsa-p256-sha256`, label `sig-b24`). The RFC 8792 `\` line wrapping is undone; nothing else changes. The
  file ends right after the 23-byte body, without a newline.
- `test-key-ecc-p256.pem`: the public key of §B.1.3 (`test-key-ecc-p256`).

Code components of an IETF document, under the Revised BSD License (RFC 9421, "Copyright Notice").
