package com.example.inventory.domain;

import jakarta.persistence.Entity;
import jakarta.persistence.Id;

/** An item kept in stock. */
@Entity
public class Item {
    @Id
    private Long id;
    private String name;
    private int quantity;

    public int getQuantity() { return quantity; }
}
