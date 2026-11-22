# RFC 9180 test vectors

`p256-base.json`: the entries of `test-vectors.json` from https://github.com/cfrg/draft-irtf-cfrg-hpke (the file RFC 9180
Appendix A is generated from), commit 5f503c564da00b0687b3de75f1dfbdfc4079ad31, whose `mode` is 0 (base) and `kem_id`
is 16 (DHKEM(P-256, HKDF-SHA256)). Entries are otherwise unmodified; the file is re-indented. The suites whose AEAD
.NET does not define (`aead_id` 65535, export-only) are skipped by the test.

Test vectors are code components of an IETF document, under the Revised BSD License (RFC 9180, "Copyright Notice").
