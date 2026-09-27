import { useEffect, useState } from 'react'
import { ItemList } from '../components/ItemList'

/** Items running low, refreshed on open. */
export default function InventoryPage() {
  const [items, setItems] = useState<string[]>([])
  useEffect(() => { fetch('/api/inventory/low').then(r => r.json()).then(setItems) }, [])
  return <ItemList items={items} />
}
