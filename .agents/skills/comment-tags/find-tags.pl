#!/usr/bin/perl
# Finds ReSharper to-do tags (TODO, PEND, INFO, IDEA, ASK…) in source comments.
#
# The tag list is read from the user's ReSharper settings (TodoPatterns in GlobalSettingsStorage.DotSettings, plus any
# *.DotSettings in the repo), so a tag added in ReSharper is picked up without touching this script. ReSharper's own
# defaults (TODO, BUG) are always included.
#
# Usage: perl find-tags.pl [--tags IDEA,ASK] [--count] [--block] [--all] [--list-tags] [path ...]
#   --tags       only these tags (comma separated, case-sensitive like ReSharper)
#   --count      print counts per tag instead of the matches
#   --block      print the whole comment block of each match: following comment lines up to "FIN <TAG>"
#                (or up to the first line that is not a comment)
#   --all        also search excluded folders (archive)
#   --list-tags  print the tags in use and where they were read from, then exit
#   path         files or folders to search; default: the root of fx the skill is in (a space: every mounted repo)
use strict; use warnings;
use File::Find;
use Cwd qw(abs_path getcwd);
use File::Basename qw(dirname);

my (%opt, @paths);
while (@ARGV) {
	my $a = shift @ARGV;
	if    ($a eq '--tags')      { $opt{tags} = shift @ARGV }
	elsif ($a eq '--count')     { $opt{count} = 1 }
	elsif ($a eq '--block')     { $opt{block} = 1 }
	elsif ($a eq '--all')       { $opt{all} = 1 }
	elsif ($a eq '--list-tags') { $opt{list} = 1 }
	else                        { push @paths, $a }
}

# The root: fx generates this skill in .claude/skills/comment-tags/ (and .agents/skills/) of the root of fx it is for.
my $root = abs_path(dirname(abs_path($0)) . '/../../..');

# --- tags from ReSharper settings --------------------------------------------------------------------------------
my %tags = (TODO => 'ReSharper default', BUG => 'ReSharper default');
my @settings;
my $appdata = $ENV{APPDATA} // '';
push @settings, "$appdata/JetBrains/Shared/vAny/GlobalSettingsStorage.DotSettings" if $appdata;
find({ no_chdir => 1, wanted => sub {
	if (-d $_ && /[\\\/](bin|obj|node_modules|\.git)$/) { $File::Find::prune = 1; return }
	push @settings, $_ if -f $_ && /\.DotSettings(\.user)?$/;
} }, $root) if -d $root;
for my $file (@settings) {
	next unless -f $file;
	open my $fh, '<', $file or next; local $/; my $xml = <$fh>; close $fh;
	while ($xml =~ m{TodoPatterns/=[^/"]+/Name/\@EntryValue">([^<]+)<}g) { $tags{$1} //= $file }
}
if ($opt{tags}) {
	my %want = map { $_ => 1 } split /\s*,\s*/, $opt{tags};
	%tags = map { $_ => ($tags{$_} // 'requested') } keys %want;
}
if ($opt{list}) { printf "%-6s %s\n", $_, $tags{$_} for sort keys %tags; exit 0 }

my $alt = join '|', map { quotemeta } sort keys %tags;
# Same shape as ReSharper's patterns: (?<=\W|^)TAG(\W|$), but only inside a comment.
my $comment = qr{(?://|/\*|<!--|^\s*\*|^\s*#)};
my $tagre   = qr{(?<=\W|^)($alt)(?=\W|$)};

# --- files ---------------------------------------------------------------------------------------------------------
@paths = ($root) unless @paths;
my @files;
my $exts = qr{\.(cs|csx|razor|cshtml|props|targets|csproj|slnx|ps1|psm1|sh|xml|json|yml|yaml|ts|js)$}i;
for my $p (@paths) {
	if (-f $p) { push @files, $p; next }
	find({ no_chdir => 1, wanted => sub {
		if (-d $_) {
			if (/[\\\/](bin|obj|node_modules|\.git|\.vs|TestResults)$/ || (!$opt{all} && /[\\\/]archive$/)) { $File::Find::prune = 1 }
			return;
		}
		push @files, $_ if $_ =~ $exts;
	} }, $p);
}

# --- search --------------------------------------------------------------------------------------------------------
my %count;
for my $file (sort @files) {
	open my $fh, '<:raw', $file or next;
	my @lines = map { s/\r?\n\z//r } <$fh>; close $fh;
	(my $shown = $file) =~ s{^\Q$root\E[\\/]?}{};
	for (my $i = 0; $i < @lines; $i++) {
		my $line = $lines[$i];
		next unless $line =~ $comment;
		my ($before, $after) = $line =~ /^(.*?)($comment.*)$/;
		next unless defined $after && $after =~ $tagre;
		my $tag = $1;
		next if $after =~ /\bFIN\s+$tag\b/;   # closing marker of a block, not a new entry
		$count{$tag}++;
		next if $opt{count};
		print "$shown:" . ($i + 1) . ": " . ($line =~ s/^\s+//r) . "\n";
		next unless $opt{block};
		for (my $j = $i + 1; $j < @lines; $j++) {
			my $next = $lines[$j];
			last unless $next =~ /^\s*(?:\/\/|\*|#|<!--)/;
			print "    " . ($next =~ s/^\s+//r) . "\n";
			last if $next =~ /\bFIN\s+$tag\b/;
		}
	}
}
if ($opt{count}) { printf "%-6s %d\n", $_, $count{$_} // 0 for sort keys %tags }
