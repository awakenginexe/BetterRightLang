# Dictionary sources

`en.txt` is derived from [wordlist-english 1.2.1](https://github.com/jacksonrayhamilton/wordlist-english/tree/462a205bafd7299ca60deadb0cdd3730fd0efa56), which distributes SCOWL. The collective work is Copyright 2000–2016 Kevin Atkinson and others; redistribution is permitted under the notices in `SCOWL-Copyright.txt`. That file contains the upstream copyright, permission, and constituent source notices.

`th.txt` is derived from [LibThai](https://github.com/tlwg/libthai/tree/e11aab1d65ca8f977e77a471dc92d07f0db02e4a), Copyright 2001–2026 Theppitak Karoonboonyanan; Copyright 2001–2005 Pattara Kiatisevi; Copyright 2001 Chanop Silpa-Anan, Poonlap Veerathanabutr, and Vuthichai Ampornaramveth. LibThai is distributed under LGPL-2.1-or-later; see `LibThai-COPYING.txt`. The linked source contains the original editable word lists. The lists are modified as described below.

To reproduce, check out the two commits above. For English, combine `sources/{english,american}-words.{10,20,35}`, decode as Latin-1, retain lines consisting only of ASCII letters, lowercase, deduplicate and sort. For Thai, combine LibThai `data/tdict-{std,std-compound,common,slang,ict}.txt`, decode as UTF-8, retain nonempty lines consisting only of Thai-block characters U+0E00–U+0E7F, deduplicate and sort. Write each result as UTF-8, one word per line with LF endings. LibThai’s `tdict-spell.txt`, which contains known misspellings, is excluded.

LibThai is a word-break dictionary. Thai phrases often contain several entries without spaces. Names, slang and newly coined words can also be absent.

`th-extra.txt` holds Thai words added by hand, such as common informal spellings that LibThai lacks. The engine loads it together with `th.txt`. A file named `en-extra.txt` beside `en.txt` would be loaded the same way.
