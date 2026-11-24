# Nid's Postgres (#582): pgvector's image (#557), plus a French Hunspell dictionary for the documents' full-text search.
# Postgres reads text search dictionaries from its own disk (tsearch_data), so they come with the server's image. The
# dictionary is Debian's hunspell-fr-comprehensive, its accents removed as unaccent removes them from the text
# (unaccent.pl): Nid's configuration unaccents a word before looking it up. Only the three files reach the final image.
FROM pgvector/pgvector:0.8.7-pg18-trixie@sha256:ab49524d18df7200b74ac9a475e1db6297fc1eb57da4c214c63d10ecc4b754fa AS dictionary
RUN apt-get update \
 && apt-get install -y --no-install-recommends hunspell-fr-comprehensive \
 && rm -rf /var/lib/apt/lists/*
COPY unaccent.pl /tmp/unaccent.pl
RUN set -eu; data=/usr/share/postgresql/$PG_MAJOR/tsearch_data; mkdir /out; \
    perl /tmp/unaccent.pl "$data/unaccent.rules" < /usr/share/hunspell/fr.dic > /out/nid_fr.dict; \
    perl /tmp/unaccent.pl "$data/unaccent.rules" < /usr/share/hunspell/fr.aff > /out/nid_fr.affix

FROM pgvector/pgvector:0.8.7-pg18-trixie@sha256:ab49524d18df7200b74ac9a475e1db6297fc1eb57da4c214c63d10ecc4b754fa
COPY --from=dictionary /out/ /usr/share/postgresql/18/tsearch_data/
# Starts as postgres (999), as the compose files run it: the entrypoint never needs gosu to drop root, and gosu goes (its
# old Go standard library is all Trivy finds in the image).
RUN rm /usr/local/bin/gosu
USER postgres
