package com.example.inventory.service;

import com.example.inventory.domain.Item;
import com.example.inventory.repository.ItemRepository;
import java.util.List;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

/** Answers stock queries from the database. */
@Service
public class DefaultInventoryService implements InventoryService {
    private final ItemRepository items;

    @Value("${inventory.low-stock:5}")
    private int threshold;

    public DefaultInventoryService(ItemRepository items) {
        this.items = items;
    }

    @Override
    public List<Item> lowStock() {
        return items.findByQuantityLessThan(threshold);
    }

    @Override
    public Item find(Long id) {
        // a comment with { braces } and "quotes"
        return items.findById(id).orElseThrow(() -> new IllegalArgumentException("no item {" + id + "}"));
    }
}
