# Nid's backup image (1F2 design §2): the Postgres 16 client, restic, sqlite3 and jq on Alpine. Built from the
# repository, never published. Not postgres:16-alpine: its VOLUME would give every backup container an anonymous volume.
FROM restic/restic:0.18.1@sha256:39d9072fb5651c80d75c7a811612eb60b4c06b32ffe87c2e9f3c7222e1797e76 AS restic

FROM alpine:3.24@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6
RUN apk add --no-cache postgresql16-client sqlite jq ca-certificates tzdata
COPY --from=restic /usr/bin/restic /usr/local/bin/restic
COPY lib.sh /usr/local/lib/nid-backup/lib.sh
COPY --chmod=0755 nid-backup /usr/local/bin/nid-backup
COPY --chmod=0755 test.sh /usr/local/bin/nid-backup-test
ENTRYPOINT ["/usr/local/bin/nid-backup"]
CMD ["schedule"]
