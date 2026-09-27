interface ItemListProps {
  /** Names to show. */
  items: string[]
  compact?: boolean
}

/** A plain list of item names. */
export const ItemList = ({ items }: ItemListProps) => <ul>{items.map(i => <li key={i}>{i}</li>)}</ul>
