# RSI fixtures

Captured from robertsspaceindustries.com on 2026-09-25 (a few requests, 2 s apart),
then anonymized with `tools/fixtures/anonymize.mjs`: markup and classes are
untouched; handles, display names, avatars, citizen numbers, bio, location and
enlistment date are placeholders. Raw captures are not committed.

| File | Source |
|---|---|
| `members-visible-hidden.json` | `getOrgMembers` page 1 of a large org: V rows (main and affiliate) and H rows |
| `members-redacted.json` | page 50 of the same org: V, R and H rows |
| `members-roles.json` | same org filtered with `rank=1`: top rank, 5 stars, roles |
| `members-invalid-org.json` | unknown SID: `ErrInvalidOrganization` |
| `members-past-last-page.json` | page 500: RSI serves at most 400 pages, later pages answer `totalrows: 0` |
| `profile-citizen.html` | `/en/citizens/{handle}`: citizen record `#100001` |
| `profile-no-citizen-record.html` | derived from `profile-citizen.html`: record value `n/a` |
| `members-all-masked.json` | derived: R and H rows taken from the two member pages above |
