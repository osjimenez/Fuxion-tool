#!/usr/bin/perl
# Aggregates a Cobertura XML (the merged one from dotnet test --coverage) by source file: line coverage, branch
# coverage and, with --ranges, the uncovered line ranges. Files are sorted by missed lines, the biggest gaps first.
# Generated files under obj/ are skipped. Paths are shown from src/ or test/ on.
# usage: perl coverage.pl <cobertura.xml> [path-regex] [--ranges]
use strict; use warnings;
my $ranges = grep { $_ eq '--ranges' } @ARGV;
my ($xml, $filter) = grep { $_ ne '--ranges' } @ARGV;
die "usage: perl coverage.pl <cobertura.xml> [path-regex] [--ranges]\n" unless $xml;
$filter //= '.';
open my $fh, '<', $xml or die "$xml: $!"; local $/; my $c = <$fh>; close $fh;
my (%hits, %branch);
while ($c =~ m{<class\b[^>]*\bfilename="([^"]+)"[^>]*>(.*?)</class>}gs) {
	my ($file, $body) = ($1, $2);
	next unless $file =~ /$filter/ && $file !~ m{[\\/]obj[\\/]};
	while ($body =~ m{<line\b([^>]*)/?>}g) {
		my $a = $1;
		my ($n) = $a =~ /number="(\d+)"/; my ($h) = $a =~ /hits="(\d+)"/;
		next unless defined $n;
		$hits{$file}{$n} = ($hits{$file}{$n} // 0) + $h;
		if ($a =~ /condition-coverage="\d+% \((\d+)\/(\d+)\)"/) {
			my $b = $branch{$file}{$n} //= [0, $2]; $b->[0] = $1 if $1 > $b->[0];
		}
	}
}
my @rows;
for my $file (keys %hits) {
	my @lines = sort { $a <=> $b } keys %{$hits{$file}};
	my @miss = grep { !$hits{$file}{$_} } @lines;
	my ($bc, $bt) = (0, 0); for (values %{$branch{$file} // {}}) { $bc += $_->[0]; $bt += $_->[1] }
	push @rows, [$file, scalar @lines, scalar @miss, $bt ? 100 * $bc / $bt : undef, \@miss];
}
for my $r (sort { $b->[2] <=> $a->[2] } @rows) {
	my ($file, $total, $miss, $bp, $m) = @$r;
	(my $short = $file) =~ s{^.*?[\\/]((?:src|test)[\\/])}{$1};
	printf "%-60s %5.1f%% lines (%d/%d missed)  %s\n", $short, 100 * ($total - $miss) / $total, $miss, $total, defined $bp ? sprintf('%5.1f%% branches', $bp) : '';
	next unless $ranges && @$m;
	my @rg; my ($s, $p) = ($m->[0], $m->[0]);
	for my $n (@$m[1..$#$m], undef) { if (defined $n && $n == $p + 1) { $p = $n; next } push @rg, $s == $p ? $s : "$s-$p"; ($s, $p) = ($n, $n) if defined $n }
	print "      uncovered: @rg\n";
}
