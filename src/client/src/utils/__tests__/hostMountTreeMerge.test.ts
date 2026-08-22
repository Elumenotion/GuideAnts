import { describe, expect, it } from 'vitest';
import type { HostMountListingDto, NotebookFolderTreeDto } from '../../types/notebook';
import {
  graftLazyMountBranches,
  mergeHostMountListingIntoTree,
} from '../hostMountTreeMerge';

describe('hostMountTreeMerge', () => {
  const baseTree: NotebookFolderTreeDto = {
    name: 'Root',
    relativePath: '',
    subFolders: [
      {
        name: 'leaf',
        relativePath: 'leaf',
        subFolders: [
          {
            name: 'd1',
            relativePath: 'leaf/d1',
            subFolders: [
              {
                name: 'd2',
                relativePath: 'leaf/d1/d2',
                subFolders: [
                  {
                    name: 'd3',
                    relativePath: 'leaf/d1/d2/d3',
                    subFolders: [],
                    files: [
                      {
                        id: 'readme',
                        fileName: 'README.md',
                        relativePath: 'leaf/d1/d2/d3/README.md',
                        fileSize: 6,
                        lastModifiedUtc: '2024-01-01T00:00:00Z',
                        fileHash: 'h',
                        index: false,
                        isIndexed: false,
                        isLinked: true,
                      },
                    ],
                  },
                ],
                files: [],
              },
            ],
            files: [],
          },
        ],
        files: [],
      },
    ],
    files: [],
  };

  it('merges one-level listing under d3 without full-tree refresh', () => {
    const listing: HostMountListingDto = {
      path: 'leaf/d1/d2/d3',
      truncated: false,
      folders: [
        { name: 'audiocpp', relativePath: 'leaf/d1/d2/d3/audiocpp' },
        { name: 'audiocpp-asr', relativePath: 'leaf/d1/d2/d3/audiocpp-asr' },
      ],
      files: [
        {
          id: 'readme',
          fileName: 'README.md',
          relativePath: 'leaf/d1/d2/d3/README.md',
          fileSize: 6,
          lastModifiedUtc: '2024-01-01T00:00:00Z',
          fileHash: 'h',
          isLinked: true,
        },
      ],
    };

    const merged = mergeHostMountListingIntoTree(baseTree, listing);
    const d3 = merged.subFolders[0].subFolders[0].subFolders[0].subFolders[0];
    expect(d3.subFolders.map((f) => f.name).sort()).toEqual(['audiocpp', 'audiocpp-asr']);
    expect(d3.files).toHaveLength(1);
  });

  it('graft preserves lazy children across poll', () => {
    const listing: HostMountListingDto = {
      path: 'leaf/d1/d2/d3',
      truncated: false,
      folders: [{ name: 'audiocpp', relativePath: 'leaf/d1/d2/d3/audiocpp' }],
      files: [],
    };
    const previous = mergeHostMountListingIntoTree(baseTree, listing);
    const complete = new Set(['leaf/d1/d2/d3']);
    const grafted = graftLazyMountBranches(baseTree, previous, complete);
    const d3 = grafted.subFolders[0].subFolders[0].subFolders[0].subFolders[0];
    expect(d3.subFolders.map((f) => f.name)).toContain('audiocpp');
  });
});
