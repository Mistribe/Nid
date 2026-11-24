# Nid's backup image (1F2 design §2): the Postgres 18 client, restic, sqlite3 and jq on Alpine. pg_dump refuses a server
# newer than itself and reads older ones, so this one serves the bundled Postgres 18 and a shared server from 16. Built
# from the repository. Not postgres:18-alpine: its VOLUME would give every backup container an anonymous volume.
FROM restic/restic:0.20.0@sha256:93999df80d4ac6cb148e82d2ef1fe6a81996f17ff535e772723538211b31555a AS restic

FROM alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
RUN apk add --no-cache postgresql18-client sqlite jq ca-certificates tzdata
COPY --from=restic /usr/bin/restic /usr/local/bin/restic
COPY lib.sh /usr/local/lib/nid-backup/lib.sh
COPY --chmod=0755 nid-backup /usr/local/bin/nid-backup
COPY --chmod=0755 test.sh /usr/local/bin/nid-backup-test
ENTRYPOINT ["/usr/local/bin/nid-backup"]
CMD ["schedule"]
