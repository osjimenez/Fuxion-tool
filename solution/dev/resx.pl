#!/usr/bin/perl
# Usage: perl resx.pl <data.tsv> <Strings.resx> <Strings.es.resx>
# data.tsv: "key<TAB>english<TAB>spanish" upserts; "-key" deletes.
use strict;
use warnings;
use utf8;
use open qw(:std :encoding(UTF-8));

my ($data, $en, $es) = @ARGV;
open my $d, '<:encoding(UTF-8)', $data or die "$data: $!";
my (@upserts, @deletes);
while (my $line = <$d>) {
	chomp $line;
	$line =~ s/\r$//;
	next if $line =~ /^\s*(#|$)/;
	if ($line =~ /^-(\S+)$/) { push @deletes, $1; next; }
	my ($key, $english, $spanish) = split /\t/, $line;
	die "bad line: $line\n" unless defined $spanish;
	push @upserts, [$key, $english, $spanish];
}
close $d;

sub esc { my $s = shift; $s =~ s/&/&amp;/g; $s =~ s/</&lt;/g; $s =~ s/>/&gt;/g; return $s; }

for my $pair ([$en, 1], [$es, 2]) {
	my ($file, $col) = @$pair;
	open my $h, '<:encoding(UTF-8)', $file or die "$file: $!";
	local $/;
	my $text = <$h>;
	close $h;
	my $nl = $text =~ /\r\n/ ? "\r\n" : "\n";
	for my $key (@deletes) {
		my $q = quotemeta $key;
		$text =~ s/[ \t]*<data name="$q" xml:space="preserve">\r?\n[ \t]*<value>.*?<\/value>\r?\n[ \t]*<\/data>\r?\n//s;
	}
	for my $u (@upserts) {
		my ($key, $value) = ($u->[0], esc($u->[$col]));
		my $q = quotemeta $key;
		if ($text =~ /<data name="$q" xml:space="preserve">/) {
			$text =~ s/(<data name="$q" xml:space="preserve">\r?\n[ \t]*<value>).*?(<\/value>)/$1$value$2/s;
		} else {
			$text =~ s/<\/root>/  <data name="$key" xml:space="preserve">$nl    <value>$value<\/value>$nl  <\/data>$nl<\/root>/;
		}
	}
	open my $w, '>:encoding(UTF-8)', $file or die "$file: $!";
	print $w $text;
	close $w;
}
print "ok\n";
