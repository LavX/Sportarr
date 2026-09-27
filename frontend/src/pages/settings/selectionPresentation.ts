export function getVisibleSelectedIds<T extends { id: number }>(
  selectedIds: ReadonlySet<number>,
  visibleItems: readonly T[],
): number[] {
  return visibleItems
    .map((item) => item.id)
    .filter((id) => selectedIds.has(id));
}
