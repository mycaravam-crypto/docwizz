package com.example.inventory.service;

import com.example.inventory.domain.Item;
import java.util.List;

/** Stock queries. */
public interface InventoryService {
    /** Items running low. */
    List<Item> lowStock();

    Item find(Long id);
}
