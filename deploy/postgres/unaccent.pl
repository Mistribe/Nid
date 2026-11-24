# #582: strips accents from a Hunspell file the way Postgres's unaccent strips them from the text, from unaccent's own
# rules (argv[0]): a dictionary read after unaccent must hold the words as unaccent leaves them. stdin to stdout, UTF-8.
use strict;
use warnings;

binmode STDIN, ':encoding(UTF-8)';
binmode STDOUT, ':encoding(UTF-8)';
open my $rules, '<:encoding(UTF-8)', $ARGV[0] or die "$ARGV[0]: $!";
my %map;
while (<$rules>) {
    chomp;
    my ($from, $to) = split /\t/;
    $map{$from} = $to // '' if defined $from && length $from;
}
while (my $line = <STDIN>) {
    $line =~ s/(.)/exists $map{$1} ? $map{$1} : $1/ge;
    print $line;
}
