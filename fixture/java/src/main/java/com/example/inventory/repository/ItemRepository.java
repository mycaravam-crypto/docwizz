package com.example.inventory.repository;

import com.example.inventory.domain.Item;
import org.springframework.data.jpa.repository.JpaRepository;

/** Stored items. */
public interface ItemRepository extends JpaRepository<Item, Long> {
    /** Items below a quantity. */
    java.util.List<Item> findByQuantityLessThan(int quantity);
}
